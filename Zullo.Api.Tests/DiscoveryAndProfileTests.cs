using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Models;
using Xunit;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;
public class DiscoveryAndProfileTests
{
    private static async Task Seed(TestApp app, int age = 28, double lng = 18.1)
    {
        await using var db = app.Db();
        db.Profiles.AddRange(new Profile { UserId = app.A, DisplayName = "A", Age = 28, Gender = "Man", Lat = 59.3, Lng = 18.0 },
            new Profile { UserId = app.B, DisplayName = "B", Age = age, Gender = "Kvinna", Lat = 59.3, Lng = lng, IsVisible = true,
                Bio = "En presentation", PromptKey = "weekend", PromptAnswer = "En tur till havet", PhotoUrls = ["https://a.test/1", "https://a.test/2"] });
        var me = await db.Users.FindAsync(app.A); me!.MatchRadiusKm = 5;
        await db.SaveChangesAsync();
    }
    [Fact]
    public async Task FeedKeepsHistoryPrivacyAndCandidateScopedPriorityWithoutPublicBoostFlags()
    {
        await using var app = await TestApp.Start();
        await Seed(app, lng: 18.01);
        var normal = Guid.NewGuid();
        var boosted = Guid.NewGuid();
        var liked = Guid.NewGuid();
        var skipped = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var reverseBlocked = Guid.NewGuid();
        var incognito = Guid.NewGuid();
        var farBoost = Guid.NewGuid();
        await using (var db = app.Db())
        {
            foreach (var id in new[] { normal, boosted, liked, skipped, blocked, reverseBlocked, incognito, farBoost })
            {
                db.Users.Add(new User { Id = id });
                db.Profiles.Add(new Profile
                {
                    UserId = id, DisplayName = "Candidate", Age = 28, Gender = "Kvinna", IsVisible = true,
                    Lat = 59.3, Lng = id == farBoost ? 40 : id == boosted ? 18.03 : 18.005,
                    PhotoUrls = ["https://a.test/1", "https://a.test/2"]
                });
            }
            db.Likes.AddRange(new Like { FromUserId = app.A, ToUserId = liked },
                new Like { FromUserId = app.B, ToUserId = app.A, IsSuperLike = true });
            db.Skips.Add(new Skip { FromUserId = app.A, ToUserId = skipped });
            db.Blocks.AddRange(new Block { FromUserId = app.A, BlockedUserId = blocked },
                new Block { FromUserId = reverseBlocked, BlockedUserId = app.A });
            db.CommerceAccounts.AddRange(new CommerceAccount { UserId = boosted, BoostUntilUtc = DateTime.UtcNow.AddMinutes(20) },
                new CommerceAccount { UserId = farBoost, BoostUntilUtc = DateTime.UtcNow.AddMinutes(20) },
                new CommerceAccount { UserId = incognito, Incognito = true, PremiumUntilUtc = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
        }
        using var client = app.Client(app.A);
        var feed = (await client.GetFromJsonAsync<JsonObject>("/swipe/feed"))!["profiles"]!.AsArray();
        Assert.Equal(new[] { app.B, boosted, normal }, feed.Select(p => Guid.Parse(p!["userId"]!.GetValue<string>())));
        Assert.True(feed[0]!["superLikedYou"]!.GetValue<bool>());
        foreach (var profile in feed)
        {
            Assert.Null(profile!["boosted"]);
            Assert.Null(profile["boostUntilUtc"]);
        }
    }

    [Fact]
    public async Task MatchedProfileRequiresMatchAndDoesNotExposeCoordinates()
    {
        await using var app = await TestApp.Start(); await Seed(app);
        using var a = app.Client(app.A); using var c = app.Client(app.C);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/profiles/{app.B}")).StatusCode);
        await app.Match();
        var p = await a.GetFromJsonAsync<JsonObject>($"/profiles/{app.B}");
        Assert.Equal("En presentation", p!["bio"]!.GetValue<string>());
        Assert.Equal(2, p["photoUrls"]!.AsArray().Count);
        Assert.Equal("weekend", p["promptKey"]!.GetValue<string>());
        Assert.Null(p["lat"]); Assert.Null(p["lng"]); Assert.Null(p["locationName"]);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/profiles/{app.B}")).StatusCode);
        await using (var db = app.Db()) { db.Matches.RemoveRange(db.Matches); await db.SaveChangesAsync(); }
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/profiles/{app.B}")).StatusCode);
    }
    [Fact]
    public async Task ReverseBlockAndClosureDenyMatchedProfile()
    {
        await using var app = await TestApp.Start(); await Seed(app); await app.Match();
        using var a = app.Client(app.A);
        await using (var db = app.Db()) { db.Blocks.Add(new Block { FromUserId = app.B, BlockedUserId = app.A }); await db.SaveChangesAsync(); }
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/profiles/{app.B}")).StatusCode);
        await using (var db = app.Db()) { db.Blocks.RemoveRange(db.Blocks); (await db.Users.FindAsync(app.B))!.IsClosed = true; await db.SaveChangesAsync(); }
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/profiles/{app.B}")).StatusCode);
    }
    [Fact]
    public async Task LocationIsOwnerScopedAndRequiresExplicitEnabledMode()
    {
        await using var app = await TestApp.Start(); await Seed(app);
        using var a = app.Client(app.A); using var b = app.Client(app.B);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/discovery-settings", new { lat = 40.4, lng = -3.7 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/discovery-settings", new { lat = 40.4 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PatchAsJsonAsync("/me/discovery-settings", new { automaticLocation = true, lat = 40.4, lng = -3.7, locationName = "Madrid, Spanien" })).StatusCode);
        var own = await a.GetFromJsonAsync<JsonObject>("/me/discovery-settings");
        Assert.Equal("Madrid, Spanien", own!["locationName"]!.GetValue<string>()); Assert.NotNull(own["locationUpdatedAtUtc"]);
        Assert.Equal("", (await b.GetFromJsonAsync<JsonObject>("/me/discovery-settings"))!["locationName"]!.GetValue<string>());
        await using var db = app.Db(); var p = await db.Profiles.SingleAsync(p => p.UserId == app.A);
        Assert.Equal(40.4, p.Lat); Assert.Equal("", p.LivePlace);
    }
    [Theory]
    [InlineData("{\"lat\":91,\"lng\":0}")]
    [InlineData("{\"minAge\":17}")]
    [InlineData("{\"minAge\":50,\"maxAge\":30}")]
    public async Task InvalidDiscoverySettingsAreRejected(string json)
    {
        await using var app = await TestApp.Start(); await Seed(app); using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/discovery-settings", JsonNode.Parse(json))).StatusCode);
    }
    [Fact]
    public async Task DistanceFallbackIsOptInAndStrictProfilesComeFirst()
    {
        await using var app = await TestApp.Start(); await Seed(app); using var a = app.Client(app.A);
        async Task<JsonArray> Feed() => (await a.GetFromJsonAsync<JsonObject>("/swipe/feed"))!["profiles"]!.AsArray();
        Assert.Empty(await Feed());
        await a.PatchAsJsonAsync("/me/discovery-settings", new { expandDistance = true });
        var wide = await Feed(); Assert.Single(wide); Assert.True(wide[0]!["outsideDistance"]!.GetValue<bool>());
        await using (var db = app.Db()) {
            db.Profiles.Add(new Profile { UserId = app.C, Age = 27, Gender = "Kvinna", Lat = 59.3, Lng = 18.01,
                IsVisible = true, PhotoUrls = ["https://a.test/c1", "https://a.test/c2"] }); await db.SaveChangesAsync();
        }
        var strict = await Feed(); Assert.Single(strict); Assert.Equal(app.C.ToString(), strict[0]!["userId"]!.GetValue<string>());
        await a.PatchAsJsonAsync("/me/discovery-settings", new { automaticLocation = true, lat = 40.4, lng = -3.7 });
        Assert.Empty(await Feed()); // Moving to Spain changes the actual discovery centre.
    }
    [Fact]
    public async Task AgeFallbackRespectsAdultGenderAndBlockingBoundaries()
    {
        await using var app = await TestApp.Start(); await Seed(app, age: 34, lng: 18.01); using var a = app.Client(app.A);
        await a.PatchAsJsonAsync("/me/discovery-settings", new { minAge = 25, maxAge = 30 });
        async Task<JsonArray> Feed() => (await a.GetFromJsonAsync<JsonObject>("/swipe/feed"))!["profiles"]!.AsArray();
        Assert.Empty(await Feed());
        await a.PatchAsJsonAsync("/me/discovery-settings", new { expandAge = true });
        Assert.True((await Feed())[0]!["outsideAge"]!.GetValue<bool>());
        await using (var db = app.Db()) { (await db.Profiles.SingleAsync(p => p.UserId == app.A)).InterestedIn = "Män"; await db.SaveChangesAsync(); }
        Assert.Empty(await Feed());
        await using (var db = app.Db()) { (await db.Profiles.SingleAsync(p => p.UserId == app.A)).InterestedIn = "Alla"; db.Blocks.Add(new Block { FromUserId = app.B, BlockedUserId = app.A }); await db.SaveChangesAsync(); }
        Assert.Empty(await Feed());
    }
    [Fact]
    public async Task PromptPairPersistsAndInvalidOrIncompleteChoicesFail()
    {
        await using var app = await TestApp.Start(); await Seed(app); using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK, (await a.PatchAsJsonAsync("/me/profile", new { promptKey = "ask_me", promptAnswer = "Min trädgård" })).StatusCode);
        Assert.Equal("Min trädgård", (await a.GetFromJsonAsync<JsonObject>("/me/profile"))!["promptAnswer"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/profile", new { promptKey = "unknown", promptAnswer = "Svar" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/profile", new { promptKey = "ask_me", promptAnswer = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/profile", new { promptAnswer = "Ensam" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PatchAsJsonAsync("/me/profile", new { promptKey = "", promptAnswer = "" })).StatusCode);
    }
    [Theory]
    [InlineData("perfect_evening")]
    [InlineData("travel_story")]
    [InlineData("food_opinion")]
    [InlineData("teach_me")]
    [InlineData("simple_plan")]
    [InlineData("surprise")]
    [InlineData("talk_all_night")]
    public async Task ExpandedPromptsAndFriendshipPersistWithoutTranslatingUserAnswers(string key)
    {
        await using var app = await TestApp.Start(); await Seed(app); using var a = app.Client(app.A);
        const string answer = "قهوة مع أصدقاء — fika med vänner";
        Assert.Equal(HttpStatusCode.OK, (await a.PatchAsJsonAsync("/me/profile", new { promptKey = key, promptAnswer = answer, intention = "Friendship", religion = "" })).StatusCode);
        var profile = (await a.GetFromJsonAsync<JsonObject>("/me/profile"))!;
        Assert.Equal(key, profile["promptKey"]!.GetValue<string>());
        Assert.Equal(answer, profile["promptAnswer"]!.GetValue<string>());
        Assert.Equal("Friendship", profile["intention"]!.GetValue<string>());
        Assert.Equal("", profile["religion"]!.GetValue<string>());
    }
    [Theory]
    [InlineData(0, 179.99, -179.99)]
    [InlineData(89.99, 0, 179)]
    public async Task NearbyProfilesAcrossDateLineOrNearPoleAreNotLost(double latitude, double ownerLongitude, double peerLongitude)
    {
        await using var app = await TestApp.Start(); await Seed(app); using var a = app.Client(app.A);
        await using (var db = app.Db()) {
            var owner = await db.Profiles.SingleAsync(p => p.UserId == app.A); owner.Lat = latitude; owner.Lng = ownerLongitude;
            var peer = await db.Profiles.SingleAsync(p => p.UserId == app.B); peer.Lat = latitude; peer.Lng = peerLongitude;
            await db.SaveChangesAsync();
        }
        var feed = await a.GetFromJsonAsync<JsonObject>("/swipe/feed");
        Assert.Single(feed!["profiles"]!.AsArray());
    }
    [Fact]
    public async Task DiscoveryFilterSaveIsAtomicAndSmallGpsChangesDoNotRequestFeedReplacement()
    {
        await using var app = await TestApp.Start(); await Seed(app); using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PatchAsJsonAsync("/me/discovery-settings", new { radiusKm = 100, minAge = 50, maxAge = 20, interestedIn = "Män" })).StatusCode);
        await using (var db = app.Db()) {
            Assert.Equal(5, (await db.Users.FindAsync(app.A))!.MatchRadiusKm);
            Assert.Equal("", (await db.Profiles.SingleAsync(p => p.UserId == app.A)).InterestedIn);
        }
        var first = await a.PatchAsJsonAsync("/me/discovery-settings", new { automaticLocation = true, lat = 59.3, lng = 18.0 });
        Assert.True((await first.Content.ReadFromJsonAsync<JsonObject>())!["locationChanged"]!.GetValue<bool>());
        var second = await a.PatchAsJsonAsync("/me/discovery-settings", new { lat = 59.3, lng = 18.0001 });
        Assert.False((await second.Content.ReadFromJsonAsync<JsonObject>())!["locationChanged"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnknownDefaultCoordinatesNeverCreateAFalseNearbyPool()
    {
        await using var app = await TestApp.Start(); await Seed(app); using var a = app.Client(app.A);
        await using (var db = app.Db()) {
            foreach (var profile in db.Profiles) { profile.Lat = 0; profile.Lng = 0; profile.LocationUpdatedAtUtc = null; }
            var user = await db.Users.FindAsync(app.A); user!.ExpandDistance = user.ExpandAge = true;
            await db.SaveChangesAsync();
        }
        async Task<JsonArray> Feed() => (await a.GetFromJsonAsync<JsonObject>("/swipe/feed"))!["profiles"]!.AsArray();
        Assert.Empty(await Feed());
        // Even a known viewer must not be shown a peer with no real position.
        await using (var db = app.Db()) { (await db.Profiles.SingleAsync(p => p.UserId == app.A)).LocationUpdatedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(); }
        Assert.Empty(await Feed());
        // (0, 0) itself is valid when explicitly supplied by a real GPS update.
        await using (var db = app.Db()) { (await db.Profiles.SingleAsync(p => p.UserId == app.B)).LocationUpdatedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(); }
        Assert.Single(await Feed());
    }

    [Fact]
    public async Task EachFallbackToggleIsAppliedAndTurningItOffRestoresStrictLimits()
    {
        await using var app = await TestApp.Start(); await Seed(app, age: 34); using var a = app.Client(app.A);
        await a.PatchAsJsonAsync("/me/discovery-settings", new { minAge = 25, maxAge = 30 });
        async Task<JsonArray> Feed() => (await a.GetFromJsonAsync<JsonObject>("/swipe/feed"))!["profiles"]!.AsArray();
        Assert.Empty(await Feed());
        await a.PatchAsJsonAsync("/me/discovery-settings", new { expandAge = true }); Assert.Empty(await Feed());
        await a.PatchAsJsonAsync("/me/discovery-settings", new { expandAge = false, expandDistance = true }); Assert.Empty(await Feed());
        await a.PatchAsJsonAsync("/me/discovery-settings", new { expandAge = true });
        var expanded = await Feed(); Assert.Single(expanded);
        Assert.True(expanded[0]!["outsideDistance"]!.GetValue<bool>()); Assert.True(expanded[0]!["outsideAge"]!.GetValue<bool>());
        await a.PatchAsJsonAsync("/me/discovery-settings", new { expandDistance = false }); Assert.Empty(await Feed());
    }

    [Fact]
    public async Task AgeFallbackNeverIncludesMinorsAndStillReturnsAnEligibleAdult()
    {
        await using var app = await TestApp.Start(); await Seed(app, age: 17, lng: 18.01); using var a = app.Client(app.A);
        await a.PatchAsJsonAsync("/me/discovery-settings", new { minAge = 18, maxAge = 18, expandAge = true });
        async Task<JsonArray> Feed() => (await a.GetFromJsonAsync<JsonObject>("/swipe/feed"))!["profiles"]!.AsArray();
        Assert.Empty(await Feed());
        await using (var db = app.Db()) { (await db.Profiles.SingleAsync(p => p.UserId == app.B)).Age = 19; await db.SaveChangesAsync(); }
        Assert.Single(await Feed());
        await a.PatchAsJsonAsync("/me/discovery-settings", new { expandAge = false }); Assert.Empty(await Feed());
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("paused")]
    [InlineData("closed")]
    [InlineData("incognito")]
    [InlineData("one-photo")]
    public async Task ExpandedFallbackNeverBypassesCandidatePrivacyOrEligibility(string restriction)
    {
        await using var app = await TestApp.Start(); await Seed(app, age: 34); using var a = app.Client(app.A);
        await a.PatchAsJsonAsync("/me/discovery-settings", new { minAge = 25, maxAge = 30, expandAge = true, expandDistance = true });
        await using (var db = app.Db()) {
            var peer = await db.Profiles.SingleAsync(p => p.UserId == app.B);
            var user = await db.Users.FindAsync(app.B);
            if (restriction == "hidden") peer.IsVisible = false;
            if (restriction == "paused") user!.IsPaused = true;
            if (restriction == "closed") user!.IsClosed = true;
            if (restriction == "one-photo") peer.PhotoUrls = ["https://a.test/one"];
            if (restriction == "incognito") db.CommerceAccounts.Add(new CommerceAccount { UserId = app.B, Incognito = true, PremiumUntilUtc = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
        }
        Assert.Empty((await a.GetFromJsonAsync<JsonObject>("/swipe/feed"))!["profiles"]!.AsArray());
    }

    [Fact]
    public async Task ARealLocationUpdateMovesTheDiscoveryCentreToTheTravelDestination()
    {
        await using var app = await TestApp.Start(); await Seed(app); using var a = app.Client(app.A);
        await using (var db = app.Db()) {
            var peer = await db.Profiles.SingleAsync(p => p.UserId == app.B); peer.Lat = 40.4168; peer.Lng = -3.7038;
            await db.SaveChangesAsync();
        }
        async Task<JsonArray> Feed() => (await a.GetFromJsonAsync<JsonObject>("/swipe/feed"))!["profiles"]!.AsArray();
        Assert.Empty(await Feed());
        var response = await a.PatchAsJsonAsync("/me/discovery-settings", new { automaticLocation = true, lat = 40.4168, lng = -3.7038, locationName = "Madrid, España" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await Feed());
    }

}
