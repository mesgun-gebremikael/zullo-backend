using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Services;
using Zullo.Api.Dtos;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class CommerceTests
{
    sealed class VerifiedStore : ICommerceVerifier
    {
        public bool Enabled => true;
        public bool Active;
        public DateTime Period = DateTime.UtcNow.AddDays(-1);
        public List<VerifiedCredit> Credits = new();
        public Task<VerifiedCommerce> Fetch(Guid user, CancellationToken cancellation = default) => Task.FromResult(
            new VerifiedCommerce(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Active ? DateTime.UtcNow.AddDays(20) : null, Active ? Period : null, Credits));
    }
    static Task<TestApp> App(VerifiedStore? store = null) => TestApp.Start(configure: services => {
        if (store != null) services.AddSingleton<ICommerceVerifier>(store);
    });
    static async Task Profiles(TestApp app)
    {
        await using var db = app.Db();
        foreach (var (id, name) in new[] { (app.A, "A"), (app.B, "B"), (app.C, "C") })
            db.Profiles.Add(new Profile { UserId = id, DisplayName = name, Gender = "Man", Age = 25, IsVisible = true,
                PhotoUrls = new() { "https://example.test/1", "https://example.test/2" } });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CheckoutDefaultsOffAndClientCannotGrantItselfCredits()
    {
        await using var app = await App();
        await Profiles(app);
        using var a = app.Client(app.A);
        var state = await (await a.PostAsJsonAsync("/commerce/sync", new { premium = true, boosts = 9999, superLikes = 9999 })).Content.ReadFromJsonAsync<CommerceState>();
        Assert.False(state!.CheckoutEnabled); Assert.False(state.Premium); Assert.Equal(0, state.SuperLikes);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await a.PostAsync("/commerce/boost", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await a.PatchAsJsonAsync("/commerce/privacy", new { hideActivity = true })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B, superLike = true })).StatusCode);
        using var anonymous = app.Client(app.A, "missing");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/commerce/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await a.PostAsJsonAsync("/commerce/webhook", new { premium = true })).StatusCode);
        await using var db = app.Db(); Assert.Empty(await db.Likes.ToListAsync());
    }

    sealed class UnavailableProvider : ICommerceVerifier
    {
        public int Calls;
        public bool Enabled => true;
        public Task<VerifiedCommerce> Fetch(Guid user, CancellationToken cancellation = default)
        { Calls++; throw new HttpRequestException("provider temporarily unavailable"); }
    }
    [Fact]
    public async Task FreeSwipingAndPresenceNeverDependOnThePurchaseProvider()
    {
        var provider = new UnavailableProvider();
        await using var app = await TestApp.Start(configure: services => services.AddSingleton<ICommerceVerifier>(provider));
        await Profiles(app);
        using var a = app.Client(app.A);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsync("/presence", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/likes/received")).StatusCode);
        Assert.Equal(0, provider.Calls);
        var state = await a.GetFromJsonAsync<CommerceState>("/commerce/state");
        Assert.False(state!.Premium); Assert.False(state.CheckoutEnabled); Assert.Equal(0, state.SuperLikes);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await a.PostAsync("/commerce/sync", null)).StatusCode);
    }

    [Fact]
    public async Task ProviderTransactionsAreGrantedExactlyOnceAcrossSyncAndNewClients()
    {
        var store = new VerifiedStore { Credits = new() { new("app_store:one", "zullo_superlikes_5", 5, 0), new("app_store:two", "zullo_boost_1", 0, 1) } };
        await using var app = await App(store);
        for (var i = 0; i < 3; i++)
        {
            using var a = app.Client(app.A);
            var state = await (await a.PostAsync("/commerce/sync", null)).Content.ReadFromJsonAsync<CommerceState>();
            Assert.Equal(5, state!.SuperLikes); Assert.Equal(1, state.Boosts);
        }
        using var b = app.Client(app.B);
        var other = await (await b.PostAsync("/commerce/sync", null)).Content.ReadFromJsonAsync<CommerceState>();
        Assert.Equal(0, other!.SuperLikes);
        await using var db = app.Db(); Assert.Equal(2, await db.PurchaseGrants.CountAsync());
    }

    [Fact]
    public async Task SignedProviderRefundRemovesCreditsOnceAndReplayDoesNotRestoreThem()
    {
        var store = new VerifiedStore { Credits = new() { new("app_store:refund", "zullo_superlikes_5", 5, 0) } };
        const string secret = "Bearer test-webhook-authorization-more-than-32-chars";
        await using var app = await TestApp.Start(configure: services => {
            services.AddSingleton<ICommerceVerifier>(store);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Commerce:Enabled"] = "true", ["Commerce:WebhookAuthorization"] = secret,
            }).Build());
        });
        using var a = app.Client(app.A);
        await a.PostAsync("/commerce/sync", null);
        async Task<HttpResponseMessage> Refund(string authorization)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/commerce/webhook");
            req.Headers.TryAddWithoutValidation("Authorization", authorization);
            req.Content = JsonContent.Create(new { @event = new { type = "CANCELLATION", app_user_id = app.A, store = "APP_STORE", transaction_id = "refund", product_id = "zullo_superlikes_5", environment = "PRODUCTION" } });
            return await a.SendAsync(req);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refund("wrong")).StatusCode);
        Assert.Equal(5, (await a.GetFromJsonAsync<CommerceState>("/commerce/state"))!.SuperLikes);
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.OK, (await Refund(secret)).StatusCode);
        Assert.Equal(0, (await (await a.PostAsync("/commerce/sync", null)).Content.ReadFromJsonAsync<CommerceState>())!.SuperLikes);
        await using var db = app.Db(); Assert.True((await db.PurchaseGrants.SingleAsync()).Refunded);
    }

    [Fact]
    public async Task WebhookPurchaseAndEarlyRefundAreIdempotentAndSandboxCannotFundProduction()
    {
        var store = new VerifiedStore();
        const string secret = "Bearer test-provider-secret-at-least-32-characters";
        await using var app = await TestApp.Start(configure: services => {
            services.AddSingleton<ICommerceVerifier>(store);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Commerce:Enabled"] = "true", ["Commerce:AllowSandbox"] = "true", ["Commerce:WebhookAuthorization"] = secret,
            }).Build());
        });
        using var a = app.Client(app.A);
        async Task Send(string type, string transaction, string environment = "PRODUCTION")
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/commerce/webhook");
            req.Headers.TryAddWithoutValidation("Authorization", secret);
            req.Content = JsonContent.Create(new { @event = new { type, app_user_id = app.A, transaction_id = transaction,
                store = "PLAY_STORE", product_id = "zullo_superlikes_5", environment } });
            Assert.Equal(HttpStatusCode.OK, (await a.SendAsync(req)).StatusCode);
        }
        await Send("NON_RENEWING_PURCHASE", "sandbox-only", "SANDBOX");
        Assert.Equal(0, (await a.GetFromJsonAsync<CommerceState>("/commerce/state"))!.SuperLikes);
        await Send("CANCELLATION", "refund-before-delivery");
        await Send("NON_RENEWING_PURCHASE", "refund-before-delivery");
        Assert.Equal(0, (await a.GetFromJsonAsync<CommerceState>("/commerce/state"))!.SuperLikes);
        await Send("NON_RENEWING_PURCHASE", "real-order");
        await Send("NON_RENEWING_PURCHASE", "real-order");
        Assert.Equal(5, (await a.GetFromJsonAsync<CommerceState>("/commerce/state"))!.SuperLikes);
    }

    [Fact]
    public async Task SuperLikeConsumesOneCreditIsVisibleToRecipientAndNeedsReciprocalMatch()
    {
        var store = new VerifiedStore { Credits = new() { new("play_store:super", "zullo_superlikes_5", 5, 0) } };
        await using var app = await App(store); await Profiles(app);
        using var a = app.Client(app.A); using var b = app.Client(app.B);
        await a.PostAsync("/commerce/sync", null); // purchase verification precedes spending
        var response = await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B, superLike = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<LikeResponseDto>())!.Matched);
        var state = await a.GetFromJsonAsync<CommerceState>("/commerce/state"); Assert.Equal(4, state!.SuperLikes);
        var likes = await b.GetFromJsonAsync<List<ReceivedLikeDto>>("/likes/received"); Assert.True(Assert.Single(likes!).IsSuperLike);
        var feed = await b.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"); Assert.True(feed!.Profiles.First(p => p.UserId == app.A).SuperLikedYou);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync($"/messages/thread-page?otherUserId={app.B}")).StatusCode);
        await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B, superLike = true });
        Assert.Equal(4, (await a.GetFromJsonAsync<CommerceState>("/commerce/state"))!.SuperLikes);
        Assert.True((await (await b.PostAsJsonAsync("/swipe/like", new { targetUserId = app.A })).Content.ReadFromJsonAsync<LikeResponseDto>())!.Matched);
    }

    [Fact]
    public async Task BlockedSuperLikeNeverConsumesCreditOrLeaksIntoLists()
    {
        var store = new VerifiedStore { Credits = new() { new("app_store:blocked", "zullo_superlikes_5", 5, 0) } };
        await using var app = await App(store); await Profiles(app);
        using var a = app.Client(app.A); using var b = app.Client(app.B);
        await b.PostAsJsonAsync("/blocks", new { blockedUserId = app.A });
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B, superLike = true })).StatusCode);
        Assert.Equal(5, (await a.GetFromJsonAsync<CommerceState>("/commerce/state"))!.SuperLikes);
        Assert.Empty((await b.GetFromJsonAsync<List<ReceivedLikeDto>>("/likes/received"))!);
    }

    [Fact]
    public async Task BoostDebitsOnceAndRespectsBlocksAndDiscoveryFilters()
    {
        var store = new VerifiedStore { Credits = new() { new("app_store:boost", "zullo_boost_1", 0, 1) } };
        await using var app = await App(store); await Profiles(app);
        using var a = app.Client(app.A); using var b = app.Client(app.B);
        var state = await (await a.PostAsync("/commerce/boost", null)).Content.ReadFromJsonAsync<CommerceState>();
        Assert.Equal(0, state!.Boosts); Assert.InRange((state.BoostUntilUtc!.Value - DateTime.UtcNow).TotalMinutes, 29, 31);
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsync("/commerce/boost", null)).StatusCode);
        Assert.Equal(app.A, (await b.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles[0].UserId);
        await b.PostAsJsonAsync("/blocks", new { blockedUserId = app.A });
        Assert.DoesNotContain((await b.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles, p => p.UserId == app.A);
    }

    [Fact]
    public async Task PremiumAllowancesDoNotRefillOnSyncOrRestartAndExpireCorrectly()
    {
        var store = new VerifiedStore { Active = true };
        await using var app = await App(store);
        await Profiles(app);
        using var a = app.Client(app.A);
        var state = await a.GetFromJsonAsync<CommerceState>("/commerce/state"); Assert.Equal(3, state!.SuperLikes); Assert.Equal(1, state.Boosts);
        await a.PostAsync("/commerce/boost", null);
        for (var i = 0; i < 2; i++) Assert.Equal(0, (await (await a.PostAsync("/commerce/sync", null)).Content.ReadFromJsonAsync<CommerceState>())!.Boosts);
        store.Active = false;
        state = await (await a.PostAsync("/commerce/sync", null)).Content.ReadFromJsonAsync<CommerceState>();
        Assert.False(state!.Premium); Assert.Equal(0, state.SuperLikes);
    }

    [Fact]
    public async Task ActivityIsMatchOnlyAndHiddenTimestampNeverLeavesServer()
    {
        var store = new VerifiedStore { Active = true };
        await using var app = await App(store); await Profiles(app); await app.Match();
        using var a = app.Client(app.A); using var b = app.Client(app.B); using var c = app.Client(app.C);
        await a.PostAsync("/presence", null);
        var presence = await b.GetFromJsonAsync<JsonElement>($"/presence/{app.A}"); Assert.True(presence.GetProperty("online").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync($"/presence/{app.A}")).StatusCode);
        await a.PatchAsJsonAsync("/commerce/privacy", new { hideActivity = true, incognito = false });
        presence = await b.GetFromJsonAsync<JsonElement>($"/presence/{app.A}"); Assert.Equal(JsonValueKind.Null, presence.GetProperty("lastActivityUtc").ValueKind);
        var matches = await b.GetFromJsonAsync<List<MatchListItemDto>>("/matches"); Assert.Null(Assert.Single(matches!).LastActivityUtc);
        await a.PostAsJsonAsync("/blocks", new { blockedUserId = app.B });
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync($"/presence/{app.A}")).StatusCode);
    }

    [Fact]
    public async Task IncognitoIsEnforcedInFeedAndRewindCannotRemoveAMatch()
    {
        var store = new VerifiedStore { Active = true };
        await using var app = await App(store); await Profiles(app);
        using var a = app.Client(app.A); using var b = app.Client(app.B);
        await a.PatchAsJsonAsync("/commerce/privacy", new { hideActivity = false, incognito = true });
        Assert.DoesNotContain((await b.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles, p => p.UserId == app.A);
        await a.PostAsJsonAsync("/swipe/skip", new { targetUserId = app.B });
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync("/swipe/rewind", null)).StatusCode);
        await a.PostAsJsonAsync("/swipe/like", new { targetUserId = app.B });
        Assert.Contains((await b.GetFromJsonAsync<SwipeFeedResponseDto>("/swipe/feed"))!.Profiles, p => p.UserId == app.A);
        await b.PostAsJsonAsync("/swipe/like", new { targetUserId = app.A });
        Assert.Equal(HttpStatusCode.Conflict, (await a.PostAsync("/swipe/rewind", null)).StatusCode);
    }
}
