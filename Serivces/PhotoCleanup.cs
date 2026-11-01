using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Services;

public class PhotoCleanup(AppDbContext db, PhotoLifecycle lifecycle, IPhotoProvider provider, ILogger<PhotoCleanup> logger)
{
    public async Task RunOnce(CancellationToken cancellation = default)
    {
        // Import outstanding legacy references into a review queue, never into automatic deletion.
        var requests = await db.AccountRemovalRequests.AsNoTracking().Where(r => !r.ExternalAssetsReviewed).OrderBy(r => r.LastCleanupCheckUtc ?? r.CreatedAtUtc).Take(20).ToListAsync(cancellation);
        foreach (var request in requests) {
            await using var transaction = await lifecycle.OwnerTransaction(request.FormerUserId);
            await lifecycle.ImportLegacyReviews(request.FormerUserId, request.PhotoUrls);
            (await db.AccountRemovalRequests.SingleAsync(r => r.Id == request.Id, cancellation)).LastCleanupCheckUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellation); await transaction.CommitAsync(cancellation);
        }
        if (!provider.Enabled) return;
        var now = DateTime.UtcNow;
        var ids = await db.OwnedPhotos.AsNoTracking().Where(p =>
            (p.State == PhotoStates.Pending || p.State == PhotoStates.Ready || p.State == PhotoStates.Uploading) &&
            p.NextAttemptUtc <= now && (p.CleanupLeaseUntilUtc == null || p.CleanupLeaseUntilUtc < now))
            .OrderBy(p => p.NextAttemptUtc).Select(p => new { p.Id, p.UserId }).Take(5).ToListAsync(cancellation);
        foreach (var candidate in ids) {
            cancellation.ThrowIfCancellationRequested();
            OwnedPhoto? claimed = null;
            var lease = Guid.NewGuid();
            await using (var transaction = await lifecycle.OwnerTransaction(candidate.UserId)) {
                var photo = await lifecycle.Locked(candidate.Id);
                if (photo == null || photo.State is PhotoStates.Deleted or PhotoStates.Review or PhotoStates.Attached || photo.NextAttemptUtc > DateTime.UtcNow || photo.CleanupLeaseUntilUtc > DateTime.UtcNow) continue;
                // The same owner lock is used when publishing a profile: a worker cannot
                // delete a draft that a concurrent profile save has just attached.
                var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == candidate.UserId, cancellation);
                if (profile != null && profile.PhotoUrls.Any(url => PhotoLifecycle.Matches(photo, url))) {
                    photo.State = PhotoStates.Attached;
                } else {
                    PhotoLifecycle.Queue(photo);
                    if (photo.NextAttemptUtc <= DateTime.UtcNow) {
                        photo.CleanupLease = lease; photo.CleanupLeaseUntilUtc = DateTime.UtcNow.AddMinutes(3);
                        photo.DeleteAttempts++; claimed = photo;
                    }
                }
                await db.SaveChangesAsync(cancellation); await transaction.CommitAsync(cancellation);
            }
            if (claimed == null) continue;
            string? error = null;
            try { await provider.Delete(claimed, cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            catch (PhotoProviderException ex) { error = ex.Code; }
            catch (Exception) { error = "cleanup_unconfirmed"; }
            await using (var transaction = await lifecycle.OwnerTransaction(candidate.UserId)) {
                var photo = await lifecycle.Locked(candidate.Id);
                if (photo == null || photo.CleanupLease != lease) continue;
                photo.CleanupLease = null; photo.CleanupLeaseUntilUtc = null;
                photo.LastErrorCode = error;
                if (error == null) {
                    var resolvedUrls = photo.Aliases.Append(photo.Url ?? "").ToHashSet(StringComparer.Ordinal);
                    var erasure = await db.AccountRemovalRequests.SingleOrDefaultAsync(r => r.FormerUserId == photo.UserId, cancellation);
                    if (erasure != null) erasure.PhotoUrls = erasure.PhotoUrls.Where(url => !resolvedUrls.Contains(url)).ToList();
                    photo.State = PhotoStates.Deleted; photo.DeletedAtUtc = DateTime.UtcNow;
                    photo.Url = null; photo.Aliases = new(); photo.FileHash = "";
                } else if (error is "ownership_mismatch" or "invalid_legacy_namespace") {
                    photo.State = PhotoStates.Review;
                } else {
                    photo.State = PhotoStates.Pending;
                    photo.NextAttemptUtc = DateTime.UtcNow.AddMinutes(Math.Min(1440, Math.Pow(2, Math.Min(photo.DeleteAttempts, 11)))).AddSeconds(Random.Shared.Next(60));
                }
                await db.SaveChangesAsync(cancellation); await transaction.CommitAsync(cancellation);
                if (error != null) logger.LogWarning("Photo cleanup deferred for {PhotoId}, code {Code}", candidate.Id, error);
            }
        }
        // A request is complete only when every owned/review asset is resolved.
        foreach (var request in requests) {
            await using var transaction = await lifecycle.OwnerTransaction(request.FormerUserId);
            var current = await db.AccountRemovalRequests.SingleAsync(r => r.Id == request.Id, cancellation);
            if (!await db.OwnedPhotos.AnyAsync(p => p.UserId == request.FormerUserId && p.State != PhotoStates.Deleted, cancellation)) {
                current.ExternalAssetsReviewed = true; current.PhotoUrls = new();
                await db.SaveChangesAsync(cancellation);
            }
            await transaction.CommitAsync(cancellation);
        }
    }
}

public class PhotoCleanupWorker(IServiceScopeFactory scopes, ILogger<PhotoCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do {
            try {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PhotoCleanup>().RunOnce(stoppingToken);
            } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Photo cleanup will retry after an interrupted database operation."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
