using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zullo.Api.Models;
using Zullo.Api.Services;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class PremiumPlanTests
{
    [Fact]
    public void AnnualBoostsRefreshEveryAnchoredMonthWithoutRolloverOrDateDrift()
    {
        var anchor = new DateTime(2026, 1, 31, 10, 15, 0, DateTimeKind.Utc);
        var account = new CommerceAccount { PremiumPeriodStartUtc = anchor, PremiumUntilUtc = anchor.AddYears(1), PurchasedBoosts = 4 };
        CommerceService.RefreshAllowances(account, anchor);
        Assert.Equal(1, account.IncludedBoosts);
        account.IncludedBoosts = 0;
        CommerceService.RefreshAllowances(account, anchor.AddDays(20));
        Assert.Equal(0, account.IncludedBoosts);
        CommerceService.RefreshAllowances(account, new DateTime(2026, 2, 28, 10, 15, 0, DateTimeKind.Utc));
        Assert.Equal(1, account.IncludedBoosts);
        account.IncludedBoosts = 0;
        CommerceService.RefreshAllowances(account, new DateTime(2026, 3, 30, 10, 15, 0, DateTimeKind.Utc));
        Assert.Equal(0, account.IncludedBoosts);
        CommerceService.RefreshAllowances(account, new DateTime(2026, 3, 31, 10, 15, 0, DateTimeKind.Utc));
        Assert.Equal(1, account.IncludedBoosts);
        CommerceService.RefreshAllowances(account, anchor.AddMonths(8));
        Assert.Equal(1, account.IncludedBoosts);
        Assert.Equal(4, account.PurchasedBoosts);
        Assert.Equal(anchor.AddMonths(9), CommerceService.BoostWindow(account, anchor.AddMonths(8))!.Value.Next);
    }

    [Fact]
    public void AllowancesDoNotRenewAfterExpiryAndPurchasedCreditsRemainSeparate()
    {
        var anchor = DateTime.UtcNow.AddYears(-1);
        var account = new CommerceAccount { PremiumPeriodStartUtc = anchor, PremiumUntilUtc = anchor.AddMonths(1), IncludedBoosts = 0, IncludedSuperLikes = 0, PurchasedBoosts = 2, PurchasedSuperLikes = 5 };
        CommerceService.RefreshAllowances(account);
        Assert.Equal(0, account.IncludedBoosts);
        Assert.Equal(0, account.IncludedSuperLikes);
        Assert.True(CommerceService.ConsumeBoost(account));
        Assert.Equal(1, account.PurchasedBoosts);
        Assert.True(CommerceService.ConsumeSuperLike(account));
        Assert.Equal(4, account.PurchasedSuperLikes);
    }

    [Fact]
    public void WeeklyAllowanceIsThreeNotCumulativeAndSyncDoesNotRefillSpentCredits()
    {
        var now = DateTime.UtcNow;
        var account = new CommerceAccount { PremiumPeriodStartUtc = now.AddDays(-1), PremiumUntilUtc = now.AddYears(1), PurchasedSuperLikes = 7 };
        CommerceService.RefreshAllowances(account, now);
        Assert.Equal(3, account.IncludedSuperLikes);
        var next = account.NextSuperLikeRefreshUtc;
        account.IncludedSuperLikes = 0;
        CommerceService.RefreshAllowances(account, now.AddDays(6));
        Assert.Equal(0, account.IncludedSuperLikes);
        CommerceService.RefreshAllowances(account, next);
        Assert.Equal(3, account.IncludedSuperLikes);
        CommerceService.RefreshAllowances(account, next.AddDays(35));
        Assert.Equal(3, account.IncludedSuperLikes);
        Assert.Equal(7, account.PurchasedSuperLikes);
    }

    [Fact]
    public async Task CatalogHasOnePremiumTierMonthlyAndAnnualWithoutEnablingCheckout()
    {
        await using var app = await TestApp.Start();
        using var client = app.Client(app.A);
        var body = await client.GetStringAsync("/commerce/catalog");
        Assert.Contains("zullo_premium_monthly", body);
        Assert.Contains("zullo_premium_annual", body);
        Assert.DoesNotContain("weekly", body);
        var state = await client.GetFromJsonAsync<CommerceState>("/commerce/state");
        Assert.False(state!.CheckoutEnabled);
        Assert.False(state.Premium);
    }

    sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.revenuecat.com", request.RequestUri!.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    static string ProviderBody(Guid user, string product, string store, bool sandbox, string until, string start) =>
        System.Text.Json.JsonSerializer.Serialize(new {
            request_date_ms = 1,
            subscriber = new {
                original_app_user_id = user.ToString(),
                entitlements = new Dictionary<string, object> { ["premium"] = new { product_identifier = product, expires_date = until, purchase_date = start } },
                subscriptions = new Dictionary<string, object> { [product] = new { store, is_sandbox = sandbox } },
                non_subscriptions = new Dictionary<string, object>()
            }
        });
    [Theory]
    [InlineData("zullo_premium_monthly", "app_store")]
    [InlineData("zullo_premium_annual", "app_store")]
    [InlineData("zullo_premium:annual", "play_store")]
    public async Task VerifierAcceptsPublishedMonthlyAndAnnualProductsOnlyFromVerifiedStore(string product, string store)
    {
        var user = Guid.NewGuid();
        var until = DateTime.UtcNow.AddYears(1).ToString("O");
        var start = DateTime.UtcNow.ToString("O");
        var body = ProviderBody(user, product, store, false, until, start);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Commerce:Enabled"] = "true", ["Commerce:RevenueCatSecretKey"] = "sk_test_only", ["Commerce:WebhookAuthorization"] = new string('x', 32) }).Build();
        var verifier = new RevenueCatVerifier(new HttpClient(new Handler(body)), config, new Environment("Production"));
        var result = await verifier.Fetch(user);
        Assert.NotNull(result.PremiumUntil);
        Assert.Empty(result.Credits);
    }

    [Fact]
    public async Task VerifierRejectsSandboxAndUnknownPremiumProductInProduction()
    {
        var user = Guid.NewGuid();
        foreach (var (product, sandbox) in new[] { ("zullo_premium_annual", true), ("unknown_lifetime", false) })
        {
            var body = ProviderBody(user, product, "app_store", sandbox, "2030-01-01T00:00:00Z", "2026-01-01T00:00:00Z");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Commerce:Enabled"] = "true", ["Commerce:RevenueCatSecretKey"] = "sk_test_only", ["Commerce:WebhookAuthorization"] = new string('x', 32) }).Build();
            var result = await new RevenueCatVerifier(new HttpClient(new Handler(body)), config, new Environment("Production")).Fetch(user);
            Assert.Null(result.PremiumUntil);
        }
    }
}
