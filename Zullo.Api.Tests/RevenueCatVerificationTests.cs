using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zullo.Api.Services;

namespace Zullo.Api.Tests;

public class RevenueCatVerificationTests
{
    sealed class EnvironmentStub(string environment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    sealed class ProviderHandler(string json, Action<HttpRequestMessage>? inspect = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    static RevenueCatVerifier Verifier(Guid user, bool sandbox, bool allowSandbox, string environment, Guid? owner = null, bool expired = false, bool grace = false)
    {
        var future = DateTime.UtcNow.AddDays(expired ? -1 : 20).ToString("O");
        var fixture = JsonSerializer.Serialize(new {
            request_date_ms = 123456L,
            subscriber = new {
                original_app_user_id = (owner ?? user).ToString(),
                entitlements = new { premium = new { product_identifier = "zullo_premium_monthly", expires_date = future, purchase_date = DateTime.UtcNow.AddDays(-5).ToString("O") } },
                subscriptions = new Dictionary<string, object> { ["zullo_premium_monthly"] = new { store = "app_store", is_sandbox = sandbox, grace_period_expires_date = grace ? DateTime.UtcNow.AddDays(1).ToString("O") : null } },
                non_subscriptions = new Dictionary<string, object> {
                    ["zullo_superlikes_5"] = new[] { new { id = "purchase", store_transaction_id = "store-transaction", store = "app_store", is_sandbox = sandbox, is_refunded = false } },
                    ["not-a-product"] = new[] { new { id = "ignored", store = "app_store", is_sandbox = false } },
                }
            }
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Commerce:Enabled"] = "true", ["Commerce:RevenueCatSecretKey"] = "sk_test_server_only",
            ["Commerce:WebhookAuthorization"] = new string('x', 40), ["Commerce:AllowSandbox"] = allowSandbox.ToString(),
        }).Build();
        return new RevenueCatVerifier(new HttpClient(new ProviderHandler(fixture, request => {
            Assert.Equal($"https://api.revenuecat.com/v1/subscribers/{user}", request.RequestUri!.ToString());
            Assert.Equal("Bearer sk_test_server_only", request.Headers.Authorization!.ToString());
        })), config, new EnvironmentStub(environment));
    }
    [Fact]
    public async Task OnlyServerProviderDataGrantsKnownProductsForTheAuthenticatedAccount()
    {
        var user = Guid.NewGuid();
        var result = await Verifier(user, false, false, "Production").Fetch(user);
        Assert.True(result.PremiumUntil > DateTime.UtcNow);
        var credit = Assert.Single(result.Credits); Assert.Equal(5, credit.SuperLikes); Assert.Equal("app_store:store-transaction", credit.Key);
    }
    sealed class MissingSubscriber : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
    [Fact]
    public async Task ANewFreeAccountDoesNotNeedToExistInRevenueCatBeforeOpeningTheStore()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Commerce:Enabled"] = "true", ["Commerce:RevenueCatSecretKey"] = "sk_server_only",
            ["Commerce:WebhookAuthorization"] = new string('x', 40),
        }).Build();
        var verifier = new RevenueCatVerifier(new HttpClient(new MissingSubscriber()), config, new EnvironmentStub("Production"));
        var result = await verifier.Fetch(Guid.NewGuid());
        Assert.Null(result.PremiumUntil); Assert.Empty(result.Credits);
    }
    [Fact]
    public async Task AnotherRevenueCatIdentityCannotBeRestoredIntoThisAccount()
    {
        var user = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Verifier(user, false, false, "Production", owner: Guid.NewGuid()).Fetch(user));
    }
    [Theory]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", false, false)]
    [InlineData("Staging", true, true)]
    public async Task SandboxCreditsNeverBecomeProductionBenefits(string environment, bool enabled, bool expected)
    {
        var user = Guid.NewGuid();
        var result = await Verifier(user, true, enabled, environment).Fetch(user);
        Assert.Equal(expected, result.PremiumUntil > DateTime.UtcNow);
        Assert.Equal(expected ? 1 : 0, result.Credits.Count);
    }
    [Fact]
    public async Task VerifiedStoreGracePeriodKeepsAccessUntilItsEnd()
    {
        var user = Guid.NewGuid();
        var result = await Verifier(user, false, false, "Production", expired: true, grace: true).Fetch(user);
        Assert.True(result.PremiumUntil > DateTime.UtcNow);
    }
    [Fact]
    public async Task ExpiredSubscriptionDoesNotGivePremium()
    {
        var user = Guid.NewGuid();
        var result = await Verifier(user, false, false, "Production", expired: true).Fetch(user);
        Assert.False(result.PremiumUntil > DateTime.UtcNow);
    }
}
