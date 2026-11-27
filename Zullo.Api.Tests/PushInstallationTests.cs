using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zullo.Api.Services;
using static Zullo.Api.Tests.ChatAuthorizationTests;

namespace Zullo.Api.Tests;

public class PushInstallationTests
{
    [Fact]
    public async Task ClaimReloadsTrackedOwnershipChangedByAnotherClaim()
    {
        await using var app = await TestApp.Start();
        var installation = Guid.NewGuid();
        await using var stale = app.Db();
        Assert.Equal(PushClaimResult.Saved, await PushInstallationService.Update(stale, app.A, "shared", installation, 1, false));
        await using (var other = app.Db())
        {
            Assert.Equal(PushClaimResult.Saved, await PushInstallationService.Update(other, app.B, "shared", Guid.NewGuid(), 1, false));
        }
        // The first context still tracks A/shared even after B claimed it.
        Assert.Equal(PushClaimResult.Saved, await PushInstallationService.Update(stale, app.A, "shared", installation, 2, false));
        await using var check = app.Db();
        Assert.True(await PushInstallationService.OwnsToken(check, app.A, "shared"));
        Assert.Null((await check.Users.FindAsync(app.B))!.DeviceToken);
        Assert.Single(await check.PushInstallations.Where(i => i.TokenHash == PushInstallationService.Hash("shared")).ToListAsync());
    }

    [Fact]
    public async Task NewerAccountClaimRejectsLateOlderClaimAndLogout()
    {
        await using var app = await TestApp.Start();
        var installation = Guid.NewGuid();
        var a = app.Client(app.A); var b = app.Client(app.B);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/me/device-token", new { token = "shared-token", installationId = installation, version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/me/device-token", new { token = "shared-token", installationId = installation, version = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/me/device-token", new { token = "shared-token", installationId = installation, version = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/me/device-token/clear", new { installationId = installation, version = 2 })).StatusCode);
        await using var db = app.Db();
        Assert.Null((await db.Users.FindAsync(app.A))!.DeviceToken);
        Assert.Equal("shared-token", (await db.Users.FindAsync(app.B))!.DeviceToken);
        var row = await db.PushInstallations.SingleAsync();
        Assert.Equal(app.B, row.UserId); Assert.Equal(3, row.Version);
    }

    [Fact]
    public async Task LogoutRetainsTombstoneAndIdenticalRetryIsIdempotent()
    {
        await using var app = await TestApp.Start();
        var installation = Guid.NewGuid(); var client = app.Client(app.A);
        var payload = new { token = "token", installationId = installation, version = 1 };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/me/device-token", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/me/device-token", payload)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/me/device-token/clear", new { installationId = installation, version = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/me/device-token/clear", new { installationId = installation, version = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/me/device-token", payload)).StatusCode);
        await using var db = app.Db();
        var row = await db.PushInstallations.SingleAsync();
        Assert.Equal(2, row.Version); Assert.Null(row.UserId); Assert.Null(row.Token); Assert.Null(row.TokenHash);
        Assert.Null((await db.Users.FindAsync(app.A))!.DeviceToken);
    }

    [Fact]
    public async Task LegacyClaimOrClearCannotOverrideVersionedDeviceOwner()
    {
        await using var app = await TestApp.Start();
        var installation = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await app.Client(app.B).PostAsJsonAsync("/me/device-token", new { token = "owned", installationId = installation, version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client(app.A).PostAsJsonAsync("/me/device-token", new { token = "owned" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client(app.B).PostAsync("/me/device-token/clear", null)).StatusCode);
        await using var db = app.Db();
        Assert.Equal("owned", (await db.Users.FindAsync(app.B))!.DeviceToken);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public async Task InvalidVersionDoesNotClaimDevice(long version)
    {
        await using var app = await TestApp.Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client(app.A).PostAsJsonAsync("/me/device-token", new { token = "token", installationId = Guid.NewGuid(), version })).StatusCode);
        await using var db = app.Db(); Assert.Empty(await db.PushInstallations.ToListAsync());
    }

    [Fact]
    public async Task TokenReassignmentHasExactlyOneCurrentOwnerAndDistinctHash()
    {
        await using var app = await TestApp.Start();
        Assert.Equal(HttpStatusCode.OK, (await app.Client(app.A).PostAsJsonAsync("/me/device-token", new { token = "token", installationId = Guid.NewGuid(), version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client(app.B).PostAsJsonAsync("/me/device-token", new { token = "token", installationId = Guid.NewGuid(), version = 1 })).StatusCode);
        await using var db = app.Db();
        Assert.Equal(1, await db.Users.CountAsync(u => u.DeviceToken == "token"));
        Assert.Equal(1, await db.PushInstallations.CountAsync(i => i.Token != null));
        Assert.True(await PushInstallationService.OwnsToken(db, app.B, "token"));
        Assert.False(await PushInstallationService.OwnsToken(db, app.A, "token"));
        Assert.Equal(64, (await db.PushInstallations.SingleAsync(i => i.Token != null)).TokenHash!.Length);
    }

    [Fact]
    public async Task AmbiguousLegacyTokenIsNotDeliveredUntilReclaimed()
    {
        await using var app = await TestApp.Start();
        await using (var db = app.Db())
        {
            (await db.Users.FindAsync(app.A))!.DeviceToken = "duplicate";
            (await db.Users.FindAsync(app.B))!.DeviceToken = "duplicate";
            await db.SaveChangesAsync();
            Assert.False(await PushInstallationService.OwnsToken(db, app.A, "duplicate"));
            Assert.False(await PushInstallationService.OwnsToken(db, app.B, "duplicate"));
        }
        Assert.Equal(HttpStatusCode.OK, (await app.Client(app.B).PostAsJsonAsync("/me/device-token", new { token = "duplicate", installationId = Guid.NewGuid(), version = 1 })).StatusCode);
        await using (var db = app.Db()) Assert.True(await PushInstallationService.OwnsToken(db, app.B, "duplicate"));
    }

    [Fact]
    public async Task ProfilelessActorCannotLikeOrCreateMatchNotifications()
    {
        await using var app = await TestApp.Start();
        Assert.Equal(HttpStatusCode.Conflict, (await app.Client(app.A).PostAsJsonAsync("/swipe/like", new { targetUserId = app.B })).StatusCode);
        await using var db = app.Db();
        Assert.Empty(await db.Likes.ToListAsync()); Assert.Empty(await db.Matches.ToListAsync()); Assert.Empty(await db.MatchNotifications.ToListAsync());
    }
    [Fact]
    public async Task ConcurrentClaimsKeepHighestVersionAndOneOwner()
    {
        await using var app = await TestApp.Start();
        var installation = Guid.NewGuid();
        // TestApp intentionally shares one SQLite connection. Initialize EF's
        // SQLite connection functions before racing writes; HTTP claims and
        // authorization are independently covered above.
        var contexts = Enumerable.Range(0, 12).Select(_ => app.Db()).ToArray();
        try
        {
            foreach (var context in contexts) _ = context.Model;
            var results = await Task.WhenAll(Enumerable.Range(1, 12).Select(version =>
                PushInstallationService.Update(contexts[version - 1], version % 2 == 0 ? app.B : app.A,
                    "same-device", installation, version, false)));
            Assert.All(results, result => Assert.True(result is PushClaimResult.Saved or PushClaimResult.Stale));
            await using var db = app.Db();
            var row = await db.PushInstallations.SingleAsync();
            Assert.Equal(12, row.Version); Assert.Equal(app.B, row.UserId);
            Assert.Equal(1, await db.Users.CountAsync(u => u.DeviceToken == "same-device"));
        }
        finally { foreach (var context in contexts) await context.DisposeAsync(); }
    }

    [Fact]
    public async Task ClosingAccountErasesBindingSecretsButKeepsVersionTombstone()
    {
        await using var app = await TestApp.Start();
        var installation = Guid.NewGuid();
        await using (var db = app.Db())
        { (await db.Users.FindAsync(app.A))!.PasswordHash = BCrypt.Net.BCrypt.HashPassword("test-password"); await db.SaveChangesAsync(); }
        Assert.Equal(HttpStatusCode.OK, (await app.Client(app.A).PostAsJsonAsync("/me/device-token", new { token = "token", installationId = installation, version = 4 })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await app.Client(app.A).PostAsJsonAsync("/me/account/removal", new { password = "test-password", confirm = true })).StatusCode);
        await using (var db = app.Db())
        { var row = await db.PushInstallations.SingleAsync(); Assert.Equal(4, row.Version); Assert.Null(row.UserId); Assert.Null(row.Token); Assert.Null(row.TokenHash); }
    }

    sealed class BlockingPush : PushNotificationService
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task SendMatchNotificationAsync(string token, string peer, string recipient, string match, CancellationToken cancellationToken = default)
        { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
    }

    [Fact]
    public async Task ReassignmentWaitsForInFlightDeliveryAndSubsequentDispatchCannotUseOldOwner()
    {
        await using var app = await TestApp.Start();
        var installation = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await app.Client(app.A).PostAsJsonAsync("/me/device-token", new { token = "delivery-token", installationId = installation, version = 1 })).StatusCode);
        await using (var db = app.Db())
        {
            var match = TestApp.Pair(app.A, app.B); db.Matches.Add(match);
            db.MatchNotifications.Add(new() { MatchId = match.Id, RecipientUserId = app.A, OtherUserId = app.B });
            await db.SaveChangesAsync();
        }
        var push = new BlockingPush();
        await using var dispatchDb = app.Db();
        var dispatch = new MatchNotificationDispatcher(dispatchDb, push, Microsoft.Extensions.Logging.Abstractions.NullLogger<MatchNotificationDispatcher>.Instance).DispatchBatch();
        await push.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using var claimDb = app.Db();
        var claim = PushInstallationService.Update(claimDb, app.B, "delivery-token", installation, 2, false);
        Assert.False(claim.IsCompleted);
        push.Release.TrySetResult();
        Assert.Equal(1, await dispatch);
        Assert.Equal(PushClaimResult.Saved, await claim);
        Assert.False(await PushInstallationService.OwnsToken(claimDb, app.A, "delivery-token"));
        Assert.True(await PushInstallationService.OwnsToken(claimDb, app.B, "delivery-token"));
    }

}
