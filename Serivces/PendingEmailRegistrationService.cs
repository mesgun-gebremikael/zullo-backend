using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Services;

public sealed class EmailVerificationFailure(string code, int status, int? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public int? RetryAfter { get; } = retryAfter;
}

public sealed record EmailChallenge(Guid ChallengeId, string Email, DateTime ExpiresAtUtc, DateTime NextSendAtUtc);

/// A pending challenge is not an account. Only its verified consumption creates
/// a User. Email-keyed transaction locks serialize retries across server replicas.
public sealed class PendingEmailRegistrationService(AppDbContext db, IConfiguration configuration,
    IVerificationEmailSender sender, TimeProvider? clock = null)
{
    private static readonly SemaphoreSlim[] LocalLocks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private static EmailChallenge Describe(PendingEmailRegistration p) => new(p.ChallengeId, p.Email, p.ExpiresAtUtc, p.NextSendAtUtc);
    private string Digest(Guid challenge, string code)
    {
        var key = configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Jwt:Key is required.");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"email-verification:{challenge:N}:{code}")));
    }
    private static SemaphoreSlim Gate(string email) => LocalLocks[SHA256.HashData(Encoding.UTF8.GetBytes(email))[0]];
    private async Task<IDbContextTransaction> Transaction(string email, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"email-registration:" + email}, 0))", ct);
        return tx;
    }
    public async Task<EmailChallenge> Begin(string email, string password, CancellationToken ct = default)
    {
        if (!sender.IsConfigured) throw new EmailVerificationFailure("verification_unavailable", 503);
        var gate = Gate(email); await gate.WaitAsync(ct);
        try
        {
            PendingEmailRegistration pending;
            string code;
            await using (var tx = await Transaction(email, ct))
            {
                if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw new EmailVerificationFailure("email_in_use", 409);
                pending = await db.PendingEmailRegistrations.SingleOrDefaultAsync(p => p.Email == email, ct)
                    ?? new PendingEmailRegistration { Email = email, CreatedAtUtc = Now };
                if (db.Entry(pending).State == EntityState.Detached) db.PendingEmailRegistrations.Add(pending);
                code = await PrepareIssue(pending, password, ct);
                // Reserve the rate budget before contacting an external server.
                // A provider timeout after acceptance must not reset send limits.
                await tx.CommitAsync(ct);
            }
            await Deliver(pending, code, ct);
            return Describe(pending);
        }
        finally { gate.Release(); }
    }
    public async Task<EmailChallenge?> Resume(string email, string password, CancellationToken ct = default)
    {
        var pending = await db.PendingEmailRegistrations.AsNoTracking().SingleOrDefaultAsync(p => p.Email == email && p.ConsumedAtUtc == null, ct);
        if (pending is null || string.IsNullOrEmpty(pending.PasswordHash) || !BCrypt.Net.BCrypt.Verify(password, pending.PasswordHash)) return null;
        // A correct pending password may resume the form, but it grants no token.
        return Describe(pending);
    }
    public async Task<EmailChallenge> Resend(Guid challenge, CancellationToken ct = default)
    {
        if (!sender.IsConfigured) throw new EmailVerificationFailure("verification_unavailable", 503);
        var email = await db.PendingEmailRegistrations.AsNoTracking().Where(p => p.ChallengeId == challenge && p.ConsumedAtUtc == null).Select(p => p.Email).SingleOrDefaultAsync(ct);
        if (email is null) throw new EmailVerificationFailure("verification_invalid", 400);
        var gate = Gate(email); await gate.WaitAsync(ct);
        try
        {
            PendingEmailRegistration pending;
            string code;
            await using (var tx = await Transaction(email, ct))
            {
                pending = await db.PendingEmailRegistrations.SingleOrDefaultAsync(p => p.ChallengeId == challenge && p.ConsumedAtUtc == null, ct)
                    ?? throw new EmailVerificationFailure("verification_invalid", 400);
                if (string.IsNullOrEmpty(pending.PasswordHash)) throw new EmailVerificationFailure("verification_invalid", 400);
                code = await PrepareIssue(pending, null, ct);
                await tx.CommitAsync(ct);
            }
            await Deliver(pending, code, ct);
            return Describe(pending);
        }
        finally { gate.Release(); }
    }
    private async Task<string> PrepareIssue(PendingEmailRegistration pending, string? password, CancellationToken ct)
    {
        var now = Now;
        if (pending.SendWindowStartedAtUtc.AddHours(1) <= now) { pending.SendWindowStartedAtUtc = now; pending.SendCount = 0; }
        if (pending.NextSendAtUtc > now) throw new EmailVerificationFailure("verification_rate_limited", 429, Math.Max(1, (int)Math.Ceiling((pending.NextSendAtUtc - now).TotalSeconds)));
        if (pending.SendCount >= 5) throw new EmailVerificationFailure("verification_rate_limited", 429, Math.Max(1, (int)Math.Ceiling((pending.SendWindowStartedAtUtc.AddHours(1) - now).TotalSeconds)));
        pending.ChallengeId = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        pending.CodeHash = Digest(pending.ChallengeId, code);
        if (password is not null) pending.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
        pending.ExpiresAtUtc = now.AddMinutes(15); pending.NextSendAtUtc = now.AddSeconds(60); pending.Attempts = 0;
        pending.SendCount++; pending.DeliveryConfirmedAtUtc = null; pending.ConsumedAtUtc = null;
        await db.SaveChangesAsync(ct);
        return code;
    }
    private async Task Deliver(PendingEmailRegistration pending, string code, CancellationToken ct)
    {
        // Only the ephemeral call argument contains the code. The durable row
        // contains hashes and cannot be verified until SMTP accepts delivery.
        try { await sender.SendCodeAsync(pending.Email, code, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new EmailVerificationFailure("verification_delivery_failed", 503); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw new EmailVerificationFailure("verification_delivery_failed", 503); }
        var delivered = Now;
        // Once SMTP accepted the message, preserve its proof even when the
        // browser closes. Do not overwrite a newer challenge on another server.
        using var confirmationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var marked = await db.PendingEmailRegistrations
            .Where(p => p.Id == pending.Id && p.ChallengeId == pending.ChallengeId && p.ConsumedAtUtc == null && p.CodeHash == pending.CodeHash)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.DeliveryConfirmedAtUtc, delivered), confirmationTimeout.Token);
        if (marked != 1) throw new EmailVerificationFailure("verification_invalid", 400);
        var confirmedProperty = db.Entry(pending).Property(p => p.DeliveryConfirmedAtUtc);
        confirmedProperty.CurrentValue = delivered;
        confirmedProperty.OriginalValue = delivered;
        confirmedProperty.IsModified = false;
    }
    public async Task<User> Verify(Guid challenge, string code, CancellationToken ct = default)
    {
        var email = await db.PendingEmailRegistrations.AsNoTracking().Where(p => p.ChallengeId == challenge).Select(p => p.Email).SingleOrDefaultAsync(ct);
        if (email is null) throw new EmailVerificationFailure("verification_invalid", 400);
        var gate = Gate(email); await gate.WaitAsync(ct);
        try
        {
            await using var tx = await Transaction(email, ct);
            var pending = await db.PendingEmailRegistrations.SingleOrDefaultAsync(p => p.ChallengeId == challenge, ct);
            if (pending is null || pending.ConsumedAtUtc != null || pending.DeliveryConfirmedAtUtc == null || pending.ExpiresAtUtc <= Now || pending.Attempts >= 5)
                throw new EmailVerificationFailure("verification_invalid", 400);
            var supplied = Digest(challenge, code);
            if (code.Length != 6 || code.Any(c => c < '0' || c > '9') || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(supplied), Convert.FromHexString(pending.CodeHash)))
            {
                pending.Attempts++;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                throw new EmailVerificationFailure("verification_invalid", 400);
            }
            if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw new EmailVerificationFailure("email_in_use", 409);
            var user = new User { Email = email, PasswordHash = pending.PasswordHash, IsVerified = true, EmailVerifiedAtUtc = Now,
                CreatedAtUtc = Now, LikesRemaining = 50, LikesResetAtUtc = Now.AddHours(12), MatchRadiusKm = 50 };
            db.Users.Add(user); pending.ConsumedAtUtc = Now; pending.PasswordHash = ""; pending.CodeHash = "";
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return user;
        }
        finally { gate.Release(); }
    }
}

/// Abandoned password hashes/challenges are short-lived, not indefinite accounts.
public sealed class PendingEmailRegistrationCleanup(IServiceScopeFactory scopes, ILogger<PendingEmailRegistrationCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); var cutoff = DateTime.UtcNow.AddHours(-24);
                await db.PendingEmailRegistrations.Where(p => p.ExpiresAtUtc < cutoff).ExecuteDeleteAsync(stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogWarning("Pending registration cleanup could not complete."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
