using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Services;

// This helper only creates/drops its own randomly named database on a local
// PostgreSQL test service. Never pass an application/production connection.
var adminConnection = Environment.GetEnvironmentVariable("ZULLO_PUSH_TEST_ADMIN_CONNECTION")
    ?? "Host=127.0.0.1;Port=5432;Database=zullo;Username=zullo";
var adminSettings = new NpgsqlConnectionStringBuilder(adminConnection);
if (adminSettings.Host is not ("127.0.0.1" or "localhost" or "::1"))
    throw new InvalidOperationException("Push concurrency verification requires a loopback-only disposable PostgreSQL service.");
var database = "zullo_push_" + Guid.NewGuid().ToString("N");
await using var admin = new NpgsqlConnection(adminConnection);
await admin.OpenAsync();
await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin).ExecuteNonQueryAsync();
try
{
    var settings = new NpgsqlConnectionStringBuilder(adminConnection) { Database = database, MaxPoolSize = 50, CommandTimeout = 15 };
    var sourceBuilder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
    sourceBuilder.EnableDynamicJson();
    await using var source = sourceBuilder.Build();
    AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(source).Options);
    await using (var schema = Db()) await schema.Database.MigrateAsync();
    var a = new User { Email = "push-a@example.test" };
    var b = new User { Email = "push-b@example.test" };
    await using (var seed = Db()) { seed.Users.AddRange(a, b); await seed.SaveChangesAsync(); }
    async Task<PushClaimResult> Claim(Guid user, string? token, Guid installation, long version, bool clear = false)
    {
        await using var db = Db();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return await PushInstallationService.Update(db, user, token, installation, version, clear, limit.Token);
    }
    async Task AssertOwner(Guid owner, string token, Guid installation, long version)
    {
        await using var db = Db();
        var binding = await db.PushInstallations.AsNoTracking().SingleAsync(i => i.Id == installation);
        if (binding.UserId != owner || binding.Token != token || binding.TokenHash != PushInstallationService.Hash(token) || binding.Version != version)
            throw new Exception("PostgreSQL versioned installation ownership is inconsistent.");
        if (await db.Users.CountAsync(u => u.DeviceToken == token) != 1 || !await PushInstallationService.OwnsToken(db, owner, token))
            throw new Exception("PostgreSQL installation owner and legacy user-token mirror disagree.");
    }
    async Task WaitForAdvisoryWaiter()
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(8))
        {
            await using var query = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = @database AND wait_event = 'advisory'", admin);
            query.Parameters.AddWithValue("database", database);
            if ((long)(await query.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(20);
        }
        throw new Exception("Expected a real PostgreSQL advisory-lock waiter; in-process fallback is not sufficient evidence.");
    }

    var shared = Guid.NewGuid();
    const string sharedToken = "pg-highest-version-token";
    var claims = Enumerable.Range(1, 24).Reverse().Select(v => Claim(v % 2 == 0 ? b.Id : a.Id, sharedToken, shared, v));
    var results = await Task.WhenAll(claims);
    if (results.Any(r => r is not (PushClaimResult.Saved or PushClaimResult.Stale))) throw new Exception("A valid concurrent claim returned an unexpected state.");
    await AssertOwner(b.Id, sharedToken, shared, 24);
    if (await Claim(a.Id, null, shared, 23, true) != PushClaimResult.Stale) throw new Exception("Late account-A logout unbound account B.");
    await AssertOwner(b.Id, sharedToken, shared, 24);
    if (await Claim(b.Id, null, shared, 25, true) != PushClaimResult.Saved) throw new Exception("Current logout failed.");
    await using (var verifyLogout = Db())
    {
        var tombstone = await verifyLogout.PushInstallations.AsNoTracking().SingleAsync(i => i.Id == shared);
        if (tombstone.Version != 25 || tombstone.UserId != null || tombstone.Token != null || tombstone.TokenHash != null)
            throw new Exception("PostgreSQL logout did not preserve only its versioned tombstone.");
    }
    if (await Claim(a.Id, sharedToken, shared, 24) != PushClaimResult.Stale) throw new Exception("A stale registration resurrected a logout tombstone.");
    if (await Claim(b.Id, null, shared, 25, true) != PushClaimResult.Saved) throw new Exception("Logout retry was not idempotent.");

    // A delivery holding the provider's token advisory lock must delay a claim
    // from another connection until the final owner check/send transaction ends.
    var blockedInstall = Guid.NewGuid();
    const string blockedToken = "pg-held-delivery-token";
    await using (var delivery = Db())
    {
        await using var tx = await delivery.Database.BeginTransactionAsync();
        await using var lease = await PushInstallationService.TokenGate(delivery, blockedToken);
        var waiting = Claim(a.Id, blockedToken, blockedInstall, 1);
        await WaitForAdvisoryWaiter();
        if (waiting.IsCompleted) throw new Exception("Ownership claim bypassed an active delivery token lock.");
        await tx.CommitAsync();
        if (await waiting != PushClaimResult.Saved) throw new Exception("Claim did not resume after delivery committed.");
    }
    await AssertOwner(a.Id, blockedToken, blockedInstall, 1);

    // Reproduce a separate replica changing ownership while an already-loaded
    // claimant waits for the token lock. Tracked old values must be reloaded:
    // assigning their original value again must still update the database.
    var staleInstall = Guid.NewGuid();
    var interveningInstall = Guid.NewGuid();
    const string staleToken = "pg-stale-tracking-token";
    if (await Claim(a.Id, staleToken, staleInstall, 1) != PushClaimResult.Saved) throw new Exception("Race seed failed.");
    await using (var external = Db())
    {
        await using var tx = await external.Database.BeginTransactionAsync();
        await using var lease = await PushInstallationService.TokenGate(external, staleToken);
        var waiting = Claim(a.Id, staleToken, staleInstall, 2);
        await WaitForAdvisoryWaiter();
        await external.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeviceToken, (string?)null));
        await external.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeviceToken, staleToken));
        await external.PushInstallations.Where(i => i.Id == staleInstall).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.UserId, (Guid?)null).SetProperty(i => i.Token, (string?)null).SetProperty(i => i.TokenHash, (string?)null));
        external.PushInstallations.Add(new PushInstallation { Id = interveningInstall, UserId = b.Id, Token = staleToken,
            TokenHash = PushInstallationService.Hash(staleToken), Version = 1, UpdatedAtUtc = DateTime.UtcNow });
        await external.SaveChangesAsync();
        await tx.CommitAsync();
        if (await waiting != PushClaimResult.Saved) throw new Exception("The newer claimant did not complete after a competing replica.");
    }
    await AssertOwner(a.Id, staleToken, staleInstall, 2);

    // A separate replica can also advance the SAME installation's version while
    // this claimant waits. The version check must use the post-lock row, not the
    // earlier tracked snapshot, and must leave the newer account's claim intact.
    await using (var external = Db())
    {
        await using var tx = await external.Database.BeginTransactionAsync();
        await using var lease = await PushInstallationService.TokenGate(external, staleToken);
        var waiting = Claim(a.Id, staleToken, staleInstall, 3);
        await WaitForAdvisoryWaiter();
        await external.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeviceToken, (string?)null));
        await external.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeviceToken, staleToken));
        await external.PushInstallations.Where(i => i.Id == staleInstall).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.UserId, (Guid?)b.Id).SetProperty(i => i.Version, 4L));
        await tx.CommitAsync();
        if (await waiting != PushClaimResult.Stale) throw new Exception("A claimant used its stale pre-lock version after another replica advanced the installation.");
    }
    await AssertOwner(b.Id, staleToken, staleInstall, 4);

    // Different installation locks still serialize a common token across real
    // database connections, with the unique digest and the legacy mirror intact.
    var owners = Enumerable.Range(0, 12).Select(i => new User { Email = $"parallel-{i}@example.test" }).ToList();
    await using (var seed = Db()) { seed.Users.AddRange(owners); await seed.SaveChangesAsync(); }
    const string contestedToken = "pg-many-installations-token";
    var races = owners.Select(u => Claim(u.Id, contestedToken, Guid.NewGuid(), 1));
    if ((await Task.WhenAll(races)).Any(r => r != PushClaimResult.Saved)) throw new Exception("A serialized distinct-installation claim unexpectedly failed.");
    await using (var verify = Db())
    {
        var binding = await verify.PushInstallations.AsNoTracking().SingleAsync(i => i.TokenHash == PushInstallationService.Hash(contestedToken));
        if (binding.UserId == null || await verify.Users.CountAsync(u => u.DeviceToken == contestedToken) != 1 ||
            !await PushInstallationService.OwnsToken(verify, binding.UserId.Value, contestedToken))
            throw new Exception("Concurrent PostgreSQL installations left ambiguous token ownership.");
        await using var metadata = new NpgsqlConnection(settings.ConnectionString);
        await metadata.OpenAsync();
        var index = (long)(await new NpgsqlCommand("SELECT COUNT(*) FROM pg_indexes WHERE schemaname='public' AND tablename='PushInstallations' AND indexname='IX_PushInstallations_TokenHash' AND indexdef LIKE '%UNIQUE%'", metadata).ExecuteScalarAsync())!;
        if (index != 1) throw new Exception("Unique installation token digest index is missing.");
    }
    await using (var duplicate = Db())
    {
        duplicate.PushInstallations.Add(new PushInstallation { Id = Guid.NewGuid(), UserId = a.Id, Token = contestedToken,
            TokenHash = PushInstallationService.Hash(contestedToken), Version = 1, UpdatedAtUtc = DateTime.UtcNow });
        try
        {
            await duplicate.SaveChangesAsync();
            throw new Exception("PostgreSQL accepted a duplicate installation token digest.");
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation) { }
    }
    Console.WriteLine("PASS: real PostgreSQL parallel claims, highest version, stale logout, tombstones, cross-connection delivery locking, stale tracked owner/version refresh and token-digest uniqueness.");
}
finally
{
    NpgsqlConnection.ClearAllPools();
    await new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
}
