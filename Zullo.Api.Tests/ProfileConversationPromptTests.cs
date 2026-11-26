using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Models;
using Zullo.Api.Services;
using Xunit;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class ProfileConversationPromptTests
{
    private static readonly ProfilePrompt[] Prompts = [new("small_joy", "قهوة مع أصدقائي ☕"), new("weekend", "海辺を歩く"), new("ask_me", "Mina tomater 🍅")];
    private static async Task SeedProfiles(TestApp app)
    {
        await using var db = app.Db();
        db.Profiles.AddRange(new Profile { UserId = app.A, DisplayName = "Owner", Age = 28, Gender = "Man" },
            new Profile { UserId = app.B, DisplayName = "Viewer", Age = 29, Gender = "Kvinna" });
        await db.SaveChangesAsync();
    }
    [Fact]
    public async Task ThreeAnswersRoundTripAndMatchedViewKeepsLegacyProjection()
    {
        await using var app = await TestApp.Start(); await SeedProfiles(app); await app.Match();
        using var owner = app.Client(app.A); using var viewer = app.Client(app.B);
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync("/me/profile", new { conversationPrompts = Prompts })).StatusCode);
        var own = await owner.GetFromJsonAsync<JsonObject>("/me/profile");
        Assert.Equal(3, own!["conversationPrompts"]!.AsArray().Count);
        Assert.Equal(Prompts[0].Key, own["promptKey"]!.GetValue<string>());
        Assert.Equal(Prompts[0].Answer, own["promptAnswer"]!.GetValue<string>());
        var visible = await viewer.GetFromJsonAsync<JsonObject>($"/profiles/{app.A}");
        Assert.Equal(Prompts[2].Answer, visible!["conversationPrompts"]![2]!["answer"]!.GetValue<string>());
        Assert.Null(visible["lat"]); Assert.Null(visible["lng"]);
        await using var db = app.Db();
        var stored = await db.Profiles.AsNoTracking().SingleAsync(p => p.UserId == app.A);
        Assert.Equal(Prompts, stored.ConversationPrompts);
    }
    [Fact]
    public async Task LegacyFirstQuestionEditAndRemovalPreserveAdditionalAnswers()
    {
        await using var app = await TestApp.Start(); await SeedProfiles(app);
        using var client = app.Client(app.A);
        await client.PatchAsJsonAsync("/me/profile", new { conversationPrompts = Prompts });
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/me/profile", new { bio = "Only biography changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/me/profile", new { promptKey = "first_date", promptAnswer = "A walk" })).StatusCode);
        var updated = await client.GetFromJsonAsync<JsonObject>("/me/profile");
        Assert.Equal("first_date", updated!["conversationPrompts"]![0]!["key"]!.GetValue<string>());
        Assert.Equal(Prompts[1].Answer, updated["conversationPrompts"]![1]!["answer"]!.GetValue<string>());
        Assert.Equal(3, updated["conversationPrompts"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/me/profile", new { promptKey = "", promptAnswer = "" })).StatusCode);
        updated = await client.GetFromJsonAsync<JsonObject>("/me/profile");
        Assert.Equal(2, updated!["conversationPrompts"]!.AsArray().Count);
        Assert.Equal("weekend", updated["promptKey"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/me/profile", new { conversationPrompts = Array.Empty<ProfilePrompt>() })).StatusCode);
        updated = await client.GetFromJsonAsync<JsonObject>("/me/profile");
        Assert.Empty(updated!["conversationPrompts"]!.AsArray()); Assert.Equal("", updated["promptKey"]!.GetValue<string>());
    }
    [Fact]
    public async Task LegacyClientCannotOverwriteAnotherSavedQuestion()
    {
        await using var app = await TestApp.Start(); await SeedProfiles(app); using var client = app.Client(app.A);
        await client.PatchAsJsonAsync("/me/profile", new { conversationPrompts = Prompts });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync("/me/profile", new { promptKey = "weekend", promptAnswer = "Would replace another answer" })).StatusCode);
        var value = await client.GetFromJsonAsync<JsonObject>("/me/profile");
        Assert.Equal(Prompts[1].Answer, value!["conversationPrompts"]![1]!["answer"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("[{\"key\":\"weekend\",\"answer\":\"One\"},{\"key\":\"weekend\",\"answer\":\"Two\"}]")]
    [InlineData("[{\"key\":\"unknown\",\"answer\":\"One\"}]")]
    [InlineData("[{\"key\":\"weekend\",\"answer\":\" \"}]")]
    [InlineData("[null]")]
    [InlineData("null")]
    [InlineData("[{\"key\":\"weekend\",\"answer\":null}]")]
    public async Task InvalidProfileCollectionIsRejectedWithoutClearingSavedAnswers(string raw)
    {
        await using var app = await TestApp.Start(); await SeedProfiles(app); using var client = app.Client(app.A);
        await client.PatchAsJsonAsync("/me/profile", new { conversationPrompts = Prompts });
        var response = await client.PatchAsJsonAsync("/me/profile", new JsonObject { ["conversationPrompts"] = JsonNode.Parse(raw) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var saved = await client.GetFromJsonAsync<JsonObject>("/me/profile");
        Assert.Equal(3, saved!["conversationPrompts"]!.AsArray().Count);
    }
    [Fact]
    public async Task MaximumLengthAndCollectionLimitAreEnforced()
    {
        await using var app = await TestApp.Start(); await SeedProfiles(app); using var client = app.Client(app.A);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync("/me/profile", new { conversationPrompts = Prompts.Concat([new ProfilePrompt("first_date", "fourth")]) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync("/me/profile", new { conversationPrompts = new[] { new ProfilePrompt("weekend", new string('x', 251)) } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync("/me/profile", new { conversationPrompts = Prompts, promptKey = "weekend", promptAnswer = "mismatched" })).StatusCode);
    }
    [Fact]
    public async Task LegacyProfileAndDraftReadAsSingleCollectionWithoutRewritingUserText()
    {
        await using var app = await TestApp.Start(); await SeedProfiles(app);
        await using (var db = app.Db()) {
            var profile = await db.Profiles.SingleAsync(p => p.UserId == app.A);
            profile.PromptKey = "ask_me"; profile.PromptAnswer = "خبرتي في السفر 🌍";
            db.OnboardingDrafts.Add(new OnboardingDraft { UserId = app.C, Step = 17, Revision = 2,
                DataJson = "{\"promptKey\":\"weekend\",\"promptAnswer\":\"海辺\",\"photoUrls\":[]}" });
            await db.SaveChangesAsync();
        }
        using var a = app.Client(app.A); using var c = app.Client(app.C);
        var own = await a.GetFromJsonAsync<JsonObject>("/me/profile");
        Assert.Single(own!["conversationPrompts"]!.AsArray());
        Assert.Equal("خبرتي في السفر 🌍", own["conversationPrompts"]![0]!["answer"]!.GetValue<string>());
        var draft = await c.GetFromJsonAsync<JsonObject>("/me/onboarding");
        Assert.Single(draft!["data"]!["conversationPrompts"]!.AsArray());
        Assert.Equal("海辺", draft["data"]!["conversationPrompts"]![0]!["answer"]!.GetValue<string>());
    }
    [Fact]
    public async Task DraftAllowsUnansweredQuestionsAndLegacyUpdatePreservesOthers()
    {
        await using var app = await TestApp.Start(); using var client = app.Client(app.C);
        var prompts = new[] { new ProfilePrompt("small_joy", ""), Prompts[1], Prompts[2] };
        var response = await client.PutAsJsonAsync("/me/onboarding", new { revision = 0, step = 17, data = new { conversationPrompts = prompts, photoUrls = Array.Empty<string>() } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<JsonObject>();
        var revision = snapshot!["revision"]!.GetValue<int>();
        response = await client.PutAsJsonAsync("/me/onboarding", new { revision, step = 17, data = new { promptKey = "first_date", promptAnswer = "Walk", photoUrls = Array.Empty<string>() } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        snapshot = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(3, snapshot!["data"]!["conversationPrompts"]!.AsArray().Count);
        Assert.Equal(Prompts[2].Answer, snapshot["data"]!["conversationPrompts"]![2]!["answer"]!.GetValue<string>());
        Assert.Equal("first_date", snapshot["data"]!["promptKey"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("[7]")]
    [InlineData("[null]")]
    [InlineData("[{\"key\":\"weekend\",\"answer\":null}]")]
    [InlineData("[{\"key\":\"weekend\",\"answer\":\"a\"},{\"key\":\"weekend\",\"answer\":\"b\"}]")]
    public async Task MalformedDraftQuestionsReturnBadRequest(string raw)
    {
        await using var app = await TestApp.Start(); using var client = app.Client(app.C);
        var payload = new { revision = 0, step = 17, data = new JsonObject { ["conversationPrompts"] = JsonNode.Parse(raw) } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/me/onboarding", payload)).StatusCode);
    }
}
