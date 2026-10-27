using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zullo.Api.Dtos;
using Zullo.Api.Models;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class ProfileIntegrityTests
{
    private static JsonObject FullProfile() => JsonNode.Parse("""
    {"displayName":"Anna","age":29,"gender":"Kvinna","bio":"Min bio",
     "intention":"Relationship","religion":"Kristen","workout":"Ofta","smoking":"Nej",
     "pets":"Har husdjur","heightCm":175,"relationshipHistory":"Varit i ett förhållande",
     "zodiacSign":"Våg","alcohol":"Ibland","cannabis":"","childrenCount":"1 barn",
     "wantChildren":"Inte säker än","workStatus":"Jobbar","studyPlace":"Universitet",
     "studySubject":"Medicin","workPlace":"Sjukhus","jobTitle":"Läkare",
     "livePlace":"Stockholm","originPlace":"Asmara","interests":["Musik","Resor"],
     "photoUrls":["https://images.example/a.jpg","https://images.example/b.jpg"],
     "lat":59.3293,"lng":18.0686,"countryCode":"SE","interestedIn":"Män","matchRadiusKm":250}
    """)!.AsObject();

    private static async Task<JsonObject> Read(HttpClient client)
    {
        var response = await client.GetAsync("/me/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static void EqualExcept(JsonObject before, JsonObject after, params string[] changed)
    {
        foreach (var item in before.Where(p => !changed.Contains(p.Key)))
            Assert.True(JsonNode.DeepEquals(item.Value, after[item.Key]), $"Unrelated field changed: {item.Key}");
    }

    [Fact]
    public async Task OnboardingRoundTripsAllFieldsAndSwedishValues()
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        var sent = FullProfile();
        var response = await client.PostAsJsonAsync("/me/profile", sent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await Read(client);
        foreach (var field in sent) Assert.True(JsonNode.DeepEquals(field.Value, saved[field.Key]), field.Key);
        Assert.True(saved["isVisible"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    public async Task FilterAndRadiusUpdatePreservesEveryOtherField(string method)
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/me/profile", FullProfile())).StatusCode);
        var before = await Read(client);
        var update = new { intention = "Date", religion = "Muslim", matchRadiusKm = 300 };
        var response = method == "PATCH" ? await client.PatchAsJsonAsync("/me/profile", update)
            : await client.PostAsJsonAsync("/me/profile", update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await Read(client);
        EqualExcept(before, after, "intention", "religion", "matchRadiusKm");
        Assert.Equal("Date", after["intention"]!.GetValue<string>());
        Assert.Equal(300, after["matchRadiusKm"]!.GetValue<int>());
    }

    [Fact]
    public async Task OlderClientEditingCoreFieldsCannotEraseExtendedDataOrLocation()
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        await client.PostAsJsonAsync("/me/profile", FullProfile());
        var before = await Read(client);
        var response = await client.PostAsJsonAsync("/me/profile", new
        {
            displayName = "  Nytt namn  ", age = 30, gender = "Kvinna", bio = " Ny bio "
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await Read(client);
        EqualExcept(before, after, "displayName", "age", "bio");
        Assert.Equal("Nytt namn", after["displayName"]!.GetValue<string>());
        Assert.Equal("Ny bio", after["bio"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExplicitClearingAndZeroCoordinatesWorkWithoutClearingOmittedFields()
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        await client.PostAsJsonAsync("/me/profile", FullProfile());
        var before = await Read(client);
        var changes = JsonNode.Parse("""{"bio":"","heightCm":null,"photoUrls":[],"lat":0,"lng":0}""");
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/me/profile", changes)).StatusCode);
        var after = await Read(client);
        EqualExcept(before, after, "bio", "heightCm", "photoUrls", "lat", "lng", "isVisible");
        Assert.Null(after["heightCm"]);
        Assert.Equal("", after["bio"]!.GetValue<string>());
        Assert.Empty(after["photoUrls"]!.AsArray());
        Assert.Equal(0, after["lat"]!.GetValue<double>());
        Assert.False(after["isVisible"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnrelatedUpdateDoesNotMakeAnExistingHiddenProfileVisible()
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        await client.PostAsJsonAsync("/me/profile", FullProfile());
        await using (var db = app.Db())
        {
            (await db.Profiles.SingleAsync()).IsVisible = false;
            await db.SaveChangesAsync();
        }
        var response = await client.PatchAsJsonAsync("/me/profile", new { religion = "Muslim" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await Read(client))["isVisible"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("{\"bio\":null}")]
    [InlineData("{\"photoUrls\":null}")]
    [InlineData("{\"interests\":[null]}")]
    [InlineData("{\"photoUrls\":[null]}")]
    [InlineData("{\"photoUrls\":[\"http://insecure.example/a.jpg\"]}")]
    [InlineData("{\"age\":17}")]
    [InlineData("{\"age\":null}")]
    [InlineData("{\"heightCm\":500}")]
    [InlineData("{\"lat\":90.1,\"lng\":18}")]
    [InlineData("{\"lat\":59}")]
    [InlineData("{\"lat\":null,\"lng\":null}")]
    [InlineData("{\"displayName\":\"   \"}")]
    [InlineData("{\"interestedIn\":\"Unknown\"}")]
    [InlineData("{\"matchRadiusKm\":301}")]
    public async Task InvalidUpdateIsRejectedWithoutChangingProfileOrRadius(string invalidJson)
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        await client.PostAsJsonAsync("/me/profile", FullProfile());
        var before = await Read(client);
        var update = JsonNode.Parse(invalidJson)!.AsObject();
        update["workPlace"] = "Must not be saved";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync("/me/profile", update)).StatusCode);
        Assert.True(JsonNode.DeepEquals(before, await Read(client)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"displayName\":\"Name\"}")]
    [InlineData("{\"displayName\":\"Name\",\"gender\":\"Man\"}")]
    public async Task NewProfileRequiresCoreFields(string body)
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/me/profile", JsonNode.Parse(body))).StatusCode);
        await using var db = app.Db();
        Assert.Empty(await db.Profiles.ToListAsync());
    }

    [Fact]
    public async Task ProfileUpdatesCannotModifyAnotherUsersData()
    {
        await using var app = await TestApp.Start();
        using var a = app.Client(app.A);
        using var b = app.Client(app.B);
        await a.PostAsJsonAsync("/me/profile", FullProfile());
        await b.PostAsJsonAsync("/me/profile", FullProfile());
        var before = await Read(b);
        await a.PatchAsJsonAsync("/me/profile", new { userId = app.B, bio = "A changes own profile" });
        Assert.True(JsonNode.DeepEquals(before, await Read(b)));
        Assert.Equal("A changes own profile", (await Read(a))["bio"]!.GetValue<string>());
        using var guest = app.Client(app.A, "missing");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await guest.PatchAsJsonAsync("/me/profile", new { bio = "Forbidden" })).StatusCode);
    }

    [Theory]
    [InlineData("Män", "Man")]
    [InlineData("Kvinnor", "Kvinna")]
    [InlineData("Alla", null)]
    [InlineData("", null)]
    public async Task DiscoveryUsesOwnPreferenceWithoutChangingLikeOrBlockRules(string preference, string? expectedGender)
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        var profile = FullProfile();
        profile["interestedIn"] = preference;
        await client.PostAsJsonAsync("/me/profile", profile);
        await using (var db = app.Db())
        {
            foreach (var (id, gender) in new[] { (app.B, "Man"), (app.C, "Kvinna") })
                db.Profiles.Add(new Profile { UserId = id, DisplayName = gender, Gender = gender, Age = 29,
                    Lat = 59.3293, Lng = 18.0686, IsVisible = true,
                    PhotoUrls = new() { "https://images.example/1", "https://images.example/2" } });
            await db.SaveChangesAsync();
        }
        var feedResponse = await client.GetAsync("/swipe/feed");
        Assert.Equal(HttpStatusCode.OK, feedResponse.StatusCode);
        var feed = (await feedResponse.Content.ReadFromJsonAsync<SwipeFeedResponseDto>())!;
        Assert.Equal(expectedGender == null ? 2 : 1, feed.Profiles.Count);
        // The API's public profile DTO does not expose the user's private preference.
        if (expectedGender != null)
            Assert.Equal(expectedGender == "Man" ? app.B : app.C, feed.Profiles.Single().UserId);
        await using (var db = app.Db())
        {
            db.Blocks.Add(new Block { FromUserId = app.B, BlockedUserId = app.A });
            await db.SaveChangesAsync();
        }
        var blocked = await client.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed");
        Assert.DoesNotContain(blocked!.Profiles, p => p.UserId == app.B);
    }
}
