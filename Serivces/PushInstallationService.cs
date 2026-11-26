using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Services;

public enum PushClaimResult { Saved, Stale, Invalid, MissingAccount }

/// Serializes device ownership with notification sends. PostgreSQL locks cover
/// replicas; bounded striped fallback locks support the SQLite test/development host.
public static class PushInstallationService
{
    private static readonly SemaphoreSlim[] InstallLocks = Enumerable.Range(0, 257).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim[] TokenLocks = Enumerable.Range(0, 257).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim SqliteGate = new(1, 1);
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static int Stripe(string key) => (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0) % 257);
    private sealed class Lease(SemaphoreSlim? gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate?.Release(); return ValueTask.CompletedTask; }
    }
    public static async Task<IAsyncDisposable> DatabaseGate(AppDbContext db, CancellationToken cancellation = default)
    {
        if (db.Database.IsNpgsql()) return new Lease(null);
        await SqliteGate.WaitAsync(cancellation);
        return new Lease(SqliteGate);
    }
    private static async Task<IAsyncDisposable> InstallGate(Guid id, CancellationToken cancellation)
    {
        var gate = InstallLocks[Stripe(id.ToString())];
        await gate.WaitAsync(cancellation);
        return new Lease(gate);
    }
    /// Must be called inside the transaction that also performs the final owner check/send.
    public static async Task<IAsyncDisposable> TokenGate(AppDbContext db, string token, CancellationToken cancellation = default)
    {
        var hash = Hash(token);
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"push-token:" + hash}, 0))", cancellation);
            return new Lease(null);
        }
        var gate = TokenLocks[Stripe(hash)];
        await gate.WaitAsync(cancellation);
        return new Lease(gate);
    }
    public static async Task<bool> OwnsToken(AppDbContext db, Guid user, string token, CancellationToken cancellation = default)
    {
        // Ambiguous legacy data is not delivered to either account; the next
        // authenticated claim resolves it without choosing an arbitrary owner.
        var owners = await db.Users.AsNoTracking().Where(u => u.DeviceToken == token).Select(u => new { u.Id, u.IsClosed }).ToListAsync(cancellation);
        if (owners.Count != 1 || owners[0].Id != user || owners[0].IsClosed) return false;
        var hash = Hash(token);
        var binding = await db.PushInstallations.AsNoTracking().SingleOrDefaultAsync(i => i.TokenHash == hash, cancellation);
        return binding == null || (binding.UserId == user && binding.Token == token);
    }

    public static async Task<PushClaimResult> Update(AppDbContext db, Guid userId, string? token,
        Guid? installationId, long? version, bool clear, CancellationToken cancellation = default)
    {
        token = token?.Trim();
        var modern = installationId.HasValue || version.HasValue;
        if (modern && (installationId == null || installationId == Guid.Empty || version is null or <= 0)) return PushClaimResult.Invalid;
        if (!clear && (string.IsNullOrEmpty(token) || token.Length > 4096)) return PushClaimResult.Invalid;
        await using var databaseGate = await DatabaseGate(db, cancellation);
        await using var installGate = await InstallGate(installationId ?? userId, cancellation);
        // The account can acquire a different previous token while this claim
        // waits for a token lock. Retry with a fresh, ordered lock set rather
        // than taking additional locks out of order or overwriting stale data.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await UpdateAttempt(db, userId, token, installationId, version, clear, modern, cancellation);
            if (result.HasValue) return result.Value;
        }
        return PushClaimResult.Stale;
    }

    private static async Task<PushClaimResult?> UpdateAttempt(AppDbContext db, Guid userId, string? token,
        Guid? installationId, long? version, bool clear, bool modern, CancellationToken cancellation)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation);
        if (db.Database.IsNpgsql())
        {
            await AccountLifecycleLock.Shared(db, userId);
            var key = "push-install:" + (installationId ?? userId).ToString();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellation);
        }
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId && !u.IsClosed, cancellation);
        if (user == null) return PushClaimResult.MissingAccount;
        var binding = modern ? await db.PushInstallations.SingleOrDefaultAsync(i => i.Id == installationId, cancellation) : null;
        // Acquire all affected tokens in deterministic order (token replacement
        // must not race dispatch to the previous token).
        var affectedTokens = new[] { token, user.DeviceToken, binding?.Token }.Where(t => !string.IsNullOrEmpty(t)).Cast<string>().Distinct().OrderBy(Hash).ToList();
        var tokenLeases = new List<IAsyncDisposable>();
        try
        {
            if (db.Database.IsNpgsql())
            {
                foreach (var affected in affectedTokens) tokenLeases.Add(await TokenGate(db, affected, cancellation));
            }
            else
            {
                foreach (var stripe in affectedTokens.Select(t => Stripe(Hash(t))).Distinct().Order())
                {
                    await TokenLocks[stripe].WaitAsync(cancellation);
                    tokenLeases.Add(new Lease(TokenLocks[stripe]));
                }
            }
            // EF may have loaded these rows before waiting for another claim.
            // Reload after the final token lock so equal-looking assignments
            // cannot accidentally skip restoring a field cleared by that claim.
            await db.Entry(user).ReloadAsync(cancellation);
            if (db.Entry(user).State == EntityState.Detached || user.IsClosed) return PushClaimResult.MissingAccount;
            if (binding != null)
            {
                await db.Entry(binding).ReloadAsync(cancellation);
                if (db.Entry(binding).State == EntityState.Detached) binding = null;
            }
            var freshTokens = new[] { token, user.DeviceToken, binding?.Token }
                .Where(t => !string.IsNullOrEmpty(t)).Cast<string>();
            if (freshTokens.Any(t => !affectedTokens.Contains(t))) return null;
            if (binding != null && version <= binding.Version)
            {
                var identical = version == binding.Version && (clear
                    ? binding.UserId == null && binding.Token == null
                    : binding.UserId == userId && binding.Token == token);
                return identical ? PushClaimResult.Saved : PushClaimResult.Stale;
            }
            if (!modern)
            {
                // Older clients cannot steal a token claimed by the versioned protocol.
                var claimedHashes = affectedTokens.Select(Hash).ToList();
                if (await db.PushInstallations.AnyAsync(i => i.TokenHash != null && claimedHashes.Contains(i.TokenHash), cancellation))
                    return PushClaimResult.Stale;
                if (clear) user.DeviceToken = null;
                else
                {
                    foreach (var other in await db.Users.Where(u => u.Id != userId && u.DeviceToken == token).ToListAsync(cancellation)) other.DeviceToken = null;
                    user.DeviceToken = token;
                }
            }
            else
            {
                binding ??= new PushInstallation { Id = installationId!.Value };
                if (db.Entry(binding).State == EntityState.Detached) db.PushInstallations.Add(binding);
                // A logout for another current owner cannot unlink their token,
                // even if a buggy caller supplies a larger sequence.
                if (clear && binding.UserId != null && binding.UserId != userId) return PushClaimResult.Stale;
                if (clear)
                {
                    if (binding.Token != null && user.DeviceToken == binding.Token) user.DeviceToken = null;
                    else if (binding.Token == null && user.DeviceToken != null)
                    {
                        var legacyHash = Hash(user.DeviceToken);
                        if (!await db.PushInstallations.AnyAsync(i => i.TokenHash == legacyHash, cancellation)) user.DeviceToken = null;
                    }
                    binding.UserId = null; binding.Token = null; binding.TokenHash = null;
                }
                else
                {
                    var hash = Hash(token!);
                    foreach (var otherBinding in await db.PushInstallations.Where(i => i.Id != binding.Id && i.TokenHash == hash).ToListAsync(cancellation))
                    { otherBinding.UserId = null; otherBinding.Token = null; otherBinding.TokenHash = null; }
                    // Flush a released unique hash before assigning it to another row.
                    await db.SaveChangesAsync(cancellation);
                    foreach (var other in await db.Users.Where(u => u.Id != userId && u.DeviceToken == token).ToListAsync(cancellation)) other.DeviceToken = null;
                    binding.UserId = userId; binding.Token = token; binding.TokenHash = hash;
                    user.DeviceToken = token;
                }
                binding.Version = version!.Value;
                binding.UpdatedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(cancellation);
            await transaction.CommitAsync(cancellation);
            return PushClaimResult.Saved;
        }
        finally
        {
            for (var i = tokenLeases.Count - 1; i >= 0; i--) await tokenLeases[i].DisposeAsync();
        }
    }
}
