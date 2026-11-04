using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zullo.Api.Controllers;
using Zullo.Api.Dtos;
using Zullo.Api.Models;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class AccountBlockLifecycleTests
{
    private static async Task Profiles(TestApp app)
    {
        await using var db = app.Db();
        (await db.Users.SingleAsync(u => u.Id == app.A)).Email = "owner@example.test";
        db.Profiles.AddRange(
            new Profile { UserId = app.A, DisplayName = "A", Age = 25, Gender = "Man", InterestedIn = "Kvinnor", IsVisible = true, PhotoUrls = new() { "https://example.test/1.jpg", "https://example.test/2.jpg" } },
            new Profile { UserId = app.B, DisplayName = "B", Age = 25, Gender = "Kvinna", IsVisible = true, PhotoUrls = new() { "https://example.test/1.jpg", "https://example.test/2.jpg" } },
            new Profile { UserId = app.C, DisplayName = "C", Age = 25, Gender = "Man", IsVisible = true, PhotoUrls = new() { "https://example.test/1.jpg", "https://example.test/2.jpg" } });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AccountEmailIsPrivateAndAvailableWithoutAProfile()
    {
        await using var app = await TestApp.Start();
        await using (var db = app.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == app.A)).Email = "owner@example.test";
            await db.SaveChangesAsync();
        }
        using var a = app.Client(app.A);
        var account = await a.GetFromJsonAsync<JsonElement>("/me/account");
        Assert.Equal("owner@example.test", account.GetProperty("email").GetString());
        Assert.Equal(app.A, account.GetProperty("userId").GetGuid());
        Assert.Equal(2, account.EnumerateObject().Count());
        using var anonymous = app.Client(app.A, "missing");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/me/account")).StatusCode);
        using var b = app.Client(app.B);
        Assert.NotEqual("owner@example.test", (await b.GetFromJsonAsync<JsonElement>("/me/account")).GetProperty("email").GetString());
    }

    [Fact]
    public async Task DiscoveryPreferencePersistsAndChangesFeedWithoutOverwritingProfile()
    {
        await using var app = await TestApp.Start();
        await Profiles(app);
        using var a = app.Client(app.A);
        var feed = await a.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed");
        Assert.Equal(app.B, Assert.Single(feed!.Profiles).UserId);
        Assert.Equal(HttpStatusCode.OK, (await a.PatchAsJsonAsync("/me/profile", new { interestedIn = "Män" })).StatusCode);
        feed = await a.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed");
        Assert.Equal(app.C, Assert.Single(feed!.Profiles).UserId);
        await using var db = app.Db();
        var saved = await db.Profiles.SingleAsync(p => p.UserId == app.A);
        Assert.Equal("Män", saved.InterestedIn);
        Assert.Equal("A", saved.DisplayName);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/profile", new { interestedIn = "invalid" })).StatusCode);
    }

    [Fact]
    public async Task BlockUnblockRequiresFreshReciprocalLikesAndNeverResurrectsOldChat()
    {
        await using var app = await TestApp.Start();
        await Profiles(app);
        await app.Match();
        await using (var db = app.Db())
        {
            db.Likes.AddRange(new Like { FromUserId = app.A, ToUserId = app.B }, new Like { FromUserId = app.B, ToUserId = app.A });
            db.Skips.Add(new Skip { FromUserId = app.B, ToUserId = app.A });
            await db.SaveChangesAsync();
        }
        using var a = app.Client(app.A);
        using var b = app.Client(app.B);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B })).StatusCode);
        var blocks = await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks");
        Assert.Equal(app.B, Assert.Single(blocks!).UserId);
        Assert.Empty((await b.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Empty((await a.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles);
        Assert.DoesNotContain((await b.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles, p => p.UserId == app.A);
        foreach (var client in new[] { a, b }) Assert.Empty((await client.GetFromJsonAsync<List<MatchListItemDto>>("/matches"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.PostAsJsonAsync("/messages/send", new { toUserId = app.A, text = "blocked", clientMessageId = "blocked" })).StatusCode);
        // B cannot remove A's outgoing block.
        Assert.Equal(HttpStatusCode.NoContent, (await b.DeleteAsync($"/blocks/{app.A}")).StatusCode);
        Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/blocks/{app.B}")).StatusCode);
        Assert.Empty((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Contains((await a.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles, p => p.UserId == app.B);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync($"/messages/thread-page?otherUserId={app.B}")).StatusCode);
        var firstLike = await (await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(firstLike.GetProperty("matched").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync($"/messages/thread-page?otherUserId={app.B}")).StatusCode);
        var secondLike = await (await b.PostAsJsonAsync("/swipe/like", new { targetUserId = app.A })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(secondLike.GetProperty("matched").GetBoolean());
        var page = await a.GetFromJsonAsync<MessagePageDto>($"/messages/thread-page?otherUserId={app.B}");
        Assert.Empty(page!.Messages);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/messages/send", new { toUserId = app.B, text = "new start", clientMessageId = "new-start" })).StatusCode);
        await using var after = app.Db();
        Assert.Contains(await after.Messages.ToListAsync(), m => m.Text == "private B-C message");
        Assert.DoesNotContain(await after.Messages.ToListAsync(), m => m.Text == "existing A-B message");
    }

    [Fact]
    public async Task MutualBlockStaysEffectiveUntilBothUnblock()
    {
        await using var app = await TestApp.Start();
        await Profiles(app);
        using var a = app.Client(app.A);
        using var b = app.Client(app.B);
        await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B });
        await b.PostAsJsonAsync("/blocks", new { blockedUserId = app.A });
        await a.DeleteAsync($"/blocks/{app.B}");
        Assert.Empty((await a.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B })).StatusCode);
        Assert.Single((await b.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        await b.DeleteAsync($"/blocks/{app.A}");
        Assert.Contains((await a.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles, p => p.UserId == app.B);
    }

    [Fact]
    public async Task BlockCardRetainsKnownIdentityWithoutExposingProfileUpdatesOrRemovedPhotos()
    {
        await using var app = await TestApp.Start();
        await Profiles(app);
        using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B })).StatusCode);
        var card = Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal("B", card.DisplayName);
        Assert.Equal(25, card.Age);
        Assert.Equal("https://example.test/1.jpg", card.PhotoUrl);
        await using (var db = app.Db()) {
            var profile = await db.Profiles.SingleAsync(p => p.UserId == app.B);
            profile.DisplayName = "Changed privately";
            profile.Age = 26;
            profile.IsVisible = false;
            profile.PhotoUrls = ["https://example.test/new.jpg"];
            (await db.Users.SingleAsync(u => u.Id == app.B)).IsPaused = true;
            await db.SaveChangesAsync();
        }
        // Retain only the identifying information already visible at block time.
        await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B });
        card = Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal("B", card.DisplayName);
        Assert.Equal(25, card.Age);
        Assert.Empty(card.PhotoUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/profiles/{app.B}")).StatusCode);
    }

    [Fact]
    public async Task ARecordedEmptyPhotoNeverPicksUpAPhotoAddedAfterTheBlock()
    {
        await using var app = await TestApp.Start();
        await Profiles(app);
        await app.Match();
        await using (var db = app.Db()) {
            var profile = await db.Profiles.SingleAsync(p => p.UserId == app.B);
            profile.PhotoUrls = [];
            profile.IsVisible = false;
            await db.SaveChangesAsync();
        }
        using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B })).StatusCode);
        var card = Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal("B", card.DisplayName);
        Assert.Empty(card.PhotoUrl);
        await using (var db = app.Db()) {
            var profile = await db.Profiles.SingleAsync(p => p.UserId == app.B);
            profile.DisplayName = "Later public name";
            profile.Age = 26;
            profile.PhotoUrls = ["https://example.test/later.jpg", "https://example.test/another.jpg"];
            profile.IsVisible = true;
            await db.SaveChangesAsync();
        }
        card = Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal("B", card.DisplayName);
        Assert.Equal(25, card.Age);
        Assert.Empty(card.PhotoUrl);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("incognito")]
    public async Task AnExistingMatchAllowsAnIdentificationSnapshotForAPrivateProfile(string privacy)
    {
        await using var app = await TestApp.Start();
        await Profiles(app);
        await app.Match();
        await using (var db = app.Db()) {
            if (privacy == "hidden") (await db.Profiles.SingleAsync(p => p.UserId == app.B)).IsVisible = false;
            if (privacy == "incognito") db.CommerceAccounts.Add(new CommerceAccount { UserId = app.B, Incognito = true, PremiumUntilUtc = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
        }
        using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B })).StatusCode);
        var card = Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal("B", card.DisplayName);
        Assert.Equal(25, card.Age);
        Assert.Equal("https://example.test/1.jpg", card.PhotoUrl);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/profiles/{app.B}")).StatusCode);
        await using var check = app.Db();
        Assert.Equal("B", (await check.Blocks.SingleAsync(b => b.FromUserId == app.A)).SnapshotDisplayName);
        Assert.False(await check.Matches.AnyAsync(m => (m.UserAId == app.A && m.UserBId == app.B) || (m.UserAId == app.B && m.UserBId == app.A)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyBlockIdentificationFallsBackOnlyToAPublicProfile(bool visible)
    {
        await using var app = await TestApp.Start();
        await Profiles(app);
        await using (var db = app.Db()) {
            (await db.Profiles.SingleAsync(p => p.UserId == app.B)).IsVisible = visible;
            db.Blocks.Add(new Block { FromUserId = app.A, BlockedUserId = app.B });
            await db.SaveChangesAsync();
        }
        using var a = app.Client(app.A);
        var card = Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal(visible ? "B" : "Profil", card.DisplayName);
        Assert.Equal(visible ? (int?)25 : null, card.Age);
        Assert.Equal(visible ? "https://example.test/1.jpg" : "", card.PhotoUrl);
        await using var check = app.Db();
        var legacy = await check.Blocks.SingleAsync(b => b.FromUserId == app.A);
        Assert.Empty(legacy.SnapshotDisplayName);
        Assert.Null(legacy.SnapshotAge);
        Assert.Empty(legacy.SnapshotPhotoUrl);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("paused")]
    [InlineData("incognito")]
    [InlineData("reverse")]
    public async Task BlockingAnInaccessiblePersonDoesNotRevealTheirIdentity(string privacy)
    {
        await using var app = await TestApp.Start();
        await Profiles(app);
        await using (var db = app.Db()) {
            if (privacy == "hidden") (await db.Profiles.SingleAsync(p => p.UserId == app.B)).IsVisible = false;
            if (privacy == "paused") (await db.Users.SingleAsync(u => u.Id == app.B)).IsPaused = true;
            if (privacy == "incognito") db.CommerceAccounts.Add(new CommerceAccount { UserId = app.B, Incognito = true, PremiumUntilUtc = DateTime.UtcNow.AddDays(1) });
            if (privacy == "reverse") db.Blocks.Add(new Block { FromUserId = app.B, BlockedUserId = app.A });
            await db.SaveChangesAsync();
        }
        using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B })).StatusCode);
        var card = Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal("Profil", card.DisplayName);
        Assert.Null(card.Age);
        Assert.Empty(card.PhotoUrl);
        await using var check = app.Db();
        var saved = await check.Blocks.SingleAsync(b => b.FromUserId == app.A);
        Assert.Empty(saved.SnapshotDisplayName);
        Assert.Null(saved.SnapshotAge);
        Assert.Empty(saved.SnapshotPhotoUrl);
    }

    [Fact]
    public async Task AReverseBlockRedactsAnExistingCardAndClosedAccountsAreNotExposed()
    {
        await using var app = await TestApp.Start();
        await Profiles(app);
        using var a = app.Client(app.A);
        using var b = app.Client(app.B);
        await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B });
        await b.PostAsJsonAsync("/blocks", new { blockedUserId = app.A });
        var card = Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal("Profil", card.DisplayName);
        Assert.Null(card.Age);
        Assert.Empty(card.PhotoUrl);
        await b.DeleteAsync($"/blocks/{app.A}");
        await using (var db = app.Db()) {
            (await db.Users.SingleAsync(u => u.Id == app.B)).IsClosed = true;
            await db.SaveChangesAsync();
        }
        card = Assert.Single((await a.GetFromJsonAsync<List<BlockedProfileDto>>("/blocks"))!);
        Assert.Equal("Profil", card.DisplayName);
        Assert.Null(card.Age);
        Assert.Empty(card.PhotoUrl);
    }

    [Fact]
    public async Task BlocksRequireAuthenticationAndValidTarget()
    {
        await using var app = await TestApp.Start();
        using var anonymous = app.Client(app.A, "missing");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/blocks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/blocks/{app.B}")).StatusCode);
        using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.A })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsJsonAsync("/blocks", new { blockedUserId = Guid.NewGuid() })).StatusCode);
    }
}
