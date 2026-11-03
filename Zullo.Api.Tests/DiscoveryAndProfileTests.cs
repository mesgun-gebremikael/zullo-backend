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

}
