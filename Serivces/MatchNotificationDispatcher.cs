using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;

namespace Zullo.Api.Services;

/// Retries committed match events. Sending is serialized with block/unmatch/closure.
public sealed class MatchNotificationDispatcher(AppDbContext db, PushNotificationService push,
    ILogger<MatchNotificationDispatcher> logger)
{
    public async Task<int> DispatchBatch(CancellationToken cancellation = default)
    {
        var now = DateTime.UtcNow;
        var ids = await db.MatchNotifications.AsNoTracking()
            .Where(n => n.SentAtUtc == null && n.CancelledAtUtc == null && n.NextAttemptAtUtc <= now)
            .OrderBy(n => n.CreatedAtUtc).Select(n => n.Id).Take(50).ToListAsync(cancellation);
        var sent = 0;
        foreach (var id in ids)
        {
            cancellation.ThrowIfCancellationRequested();
            var candidate = await db.MatchNotifications.AsNoTracking().SingleOrDefaultAsync(n => n.Id == id, cancellation);
            if (candidate == null) continue;
            await using var databaseGate = await PushInstallationService.DatabaseGate(db, cancellation);
            await using var transaction = await RelationTransaction.Begin(db, candidate.RecipientUserId, candidate.OtherUserId);
            // Another worker may have completed the item while this worker waited.
            var row = await db.MatchNotifications.SingleOrDefaultAsync(n => n.Id == id, cancellation);
            if (row == null || row.SentAtUtc != null || row.CancelledAtUtc != null || row.NextAttemptAtUtc > DateTime.UtcNow)
                continue;
            var active = await db.Matches.AnyAsync(m => m.Id == row.MatchId &&
                ((m.UserAId == row.RecipientUserId && m.UserBId == row.OtherUserId) ||
                 (m.UserBId == row.RecipientUserId && m.UserAId == row.OtherUserId)), cancellation);
            var blocked = await db.Blocks.AnyAsync(b =>
                (b.FromUserId == row.RecipientUserId && b.BlockedUserId == row.OtherUserId) ||
                (b.FromUserId == row.OtherUserId && b.BlockedUserId == row.RecipientUserId), cancellation);
            var activeUsers = await db.Users.CountAsync(u => (u.Id == row.RecipientUserId || u.Id == row.OtherUserId) && !u.IsClosed, cancellation);
            if (!active || blocked || activeUsers != 2 || row.CreatedAtUtc < DateTime.UtcNow.AddDays(-1))
            {
                row.CancelledAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellation);
                await transaction.CommitAsync(cancellation);
                continue;
            }
            var token = await db.Users.Where(u => u.Id == row.RecipientUserId).Select(u => u.DeviceToken).SingleAsync(cancellation);
            try
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    row.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1);
                }
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await using var tokenGate = await PushInstallationService.TokenGate(db, token, timeout.Token);
                    if (await PushInstallationService.OwnsToken(db, row.RecipientUserId, token, timeout.Token))
                    {
                        await push.SendMatchNotificationAsync(token, row.OtherUserId.ToString(), row.RecipientUserId.ToString(), row.MatchId.ToString(), timeout.Token);
                        row.SentAtUtc = DateTime.UtcNow;
                        sent++;
                    }
                    else row.NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(1);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Never log FCM exception details: they can contain a device token.
                row.Attempts++;
                row.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Min(row.Attempts, 8))));
                logger.LogWarning("Match notification delivery will be retried.");
            }
            await db.SaveChangesAsync(cancellation);
            await transaction.CommitAsync(cancellation);
            db.ChangeTracker.Clear();
        }
        return sent;
    }
}

public sealed class MatchNotificationWorker(IServiceScopeFactory scopes, ILogger<MatchNotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MatchNotificationDispatcher>().DispatchBatch(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Match notification queue is temporarily unavailable."); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
