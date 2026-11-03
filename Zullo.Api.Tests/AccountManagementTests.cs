using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zullo.Api.Models;
using Zullo.Api.Dtos;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;
namespace Zullo.Api.Tests;

public class AccountManagementTests
{
    static async Task Seed(TestApp app)
    {
        await using var db = app.Db();
        foreach (var id in new[] { app.A, app.B }) {
            var user = await db.Users.SingleAsync(u => u.Id == id);
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword("test-password");
            db.Profiles.Add(new Profile { UserId = id, DisplayName = "Visible", Age = 25, Gender = "Man", IsVisible = true,
                InterestedIn = "Alla", PhotoUrls = new() { "https://example.test/a", "https://example.test/b" } });
        }
        await db.SaveChangesAsync();
    }
    [Fact]
    public async Task PauseHidesDiscoveryAndLikesWithoutRemovingMatchesAndCanBeReactivated()
    {
        await using var app = await TestApp.Start(); await Seed(app);
        using var a = app.Client(app.A); using var b = app.Client(app.B);
        await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B });
        var response = await a.PatchAsJsonAsync("/me/account/pause", new { paused = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await a.GetFromJsonAsync<JsonElement>("/me/account/status")).GetProperty("paused").GetBoolean());
        var feed = await a.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed");
        Assert.True(feed!.IsPaused); Assert.Empty(feed.Profiles);
        Assert.DoesNotContain((await b.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles, p => p.UserId == app.A);
        Assert.Equal(0, (await b.GetFromJsonAsync<JsonElement>("/likes/received/summary")).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.C })).StatusCode);
        await app.Match();
        Assert.Single((await a.GetFromJsonAsync<List<MatchListItemDto>>("/matches"))!);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/messages/thread?otherUserId={app.B}")).StatusCode);
        await a.PatchAsJsonAsync("/me/account/pause", new { paused = false });
        Assert.False((await a.GetFromJsonAsync<JsonElement>("/me/account/status")).GetProperty("paused").GetBoolean());
    }
    [Fact]
    public async Task RemovalRequiresPasswordAndConfirmationThenRevokesTokensAndRemovesPublicData()
    {
        await using var app = await TestApp.Start(); await Seed(app); await app.Match();
        using var a = app.Client(app.A); using var b = app.Client(app.B);
        await using (var db = app.Db()) {
            db.Messages.Add(new Message { FromUserId = app.A, ToUserId = app.B, Text = "private conversation" });
            db.Blocks.AddRange(
                new Block { FromUserId = app.A, BlockedUserId = app.B, SnapshotDisplayName = "B", SnapshotAge = 25, SnapshotPhotoUrl = "https://example.test/b" },
                new Block { FromUserId = app.C, BlockedUserId = app.A, SnapshotDisplayName = "A", SnapshotAge = 25, SnapshotPhotoUrl = "https://example.test/a" });
            db.PurchaseGrants.Add(new PurchaseGrant { TransactionKey = "app_store:retained-order", UserId = app.A, ProductId = "zullo_boost_1", Boosts = 1 });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/me/account/removal", new { password = "wrong", confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/me/account/removal", new { password = "test-password", confirm = false })).StatusCode);
        Assert.True((await a.GetAsync("/me/account")).IsSuccessStatusCode);
        var response = await a.PostAsJsonAsync("/me/account/removal", new { password = "test-password", confirm = true });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("cleanupPending").GetBoolean()); // Never falsely claim external images erased.
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/me/account")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.GetAsync("/commerce/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.PostAsJsonAsync("/me/profile", new { displayName = "resurrect" })).StatusCode);
        Assert.Empty((await b.GetFromJsonAsync<List<MatchListItemDto>>("/matches"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync($"/messages/thread?otherUserId={app.A}")).StatusCode);
        await using var check = app.Db();
        var closed = await check.Users.SingleAsync(u => u.Id == app.A);
        Assert.True(closed.IsClosed); Assert.Null(closed.Email); Assert.Null(closed.DeviceToken); Assert.Empty(closed.PasswordHash);
        Assert.False(await check.Profiles.AnyAsync(p => p.UserId == app.A));
        Assert.False(await check.Blocks.AnyAsync(b => b.FromUserId == app.A || b.BlockedUserId == app.A));
        Assert.False(await check.Messages.AnyAsync(m => m.FromUserId == app.A || m.ToUserId == app.A));
        Assert.True(await check.PurchaseGrants.AnyAsync(g => g.TransactionKey == "app_store:retained-order"));
        var task = await check.AccountRemovalRequests.SingleAsync(r => r.FormerUserId == app.A);
        Assert.Equal(2, task.PhotoUrls.Count); Assert.False(task.ExternalAssetsReviewed);
    }
    [Fact]
    public async Task RemovalPasswordAttemptsAreLimitedPerAccount()
    {
        await using var app = await TestApp.Start(); await Seed(app);
        using var a = app.Client(app.A);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/me/account/removal", new { password = "wrong", confirm = true })).StatusCode);
        var limited = await a.PostAsJsonAsync("/me/account/removal", new { password = "test-password", confirm = true });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode); Assert.NotNull(limited.Headers.RetryAfter);
        Assert.True((await a.GetAsync("/me/account")).IsSuccessStatusCode);
    }
}
