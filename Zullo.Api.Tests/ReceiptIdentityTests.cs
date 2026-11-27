using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using Zullo.Api.Models;
using Zullo.Api.Services;
using TestApp = Zullo.Api.Tests.ChatAuthorizationTests.TestApp;

namespace Zullo.Api.Tests;

public class ReceiptIdentityTests
{
    sealed class Store : ICommerceVerifier
    {
        public bool Enabled => true;
        public List<VerifiedCredit> Credits = new();
        public Task<VerifiedCommerce> Fetch(Guid user, CancellationToken cancellation = default) => Task.FromResult(
            new VerifiedCommerce(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, null, Credits));
    }
    static async Task<(bool Verified, bool Refunded)> Status(HttpClient client, string id,
        string kind = "revenuecat", string product = "zullo_superlikes_5", string store = "app_store")
    {
        var response = await client.PostAsJsonAsync("/commerce/purchase-status", new { store, transactionId = id, productId = product, identifierKind = kind });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("verified").GetBoolean(), json.RootElement.GetProperty("refunded").GetBoolean());
    }

    [Fact]
    public async Task SdkHistoryProviderIdResolvesOnlyThroughVerifiedOwnStoreLedger()
    {
        var store = new Store { Credits = new() { new("app_store:store-order", "zullo_superlikes_5", 5, 0, "rc-history") } };
        await using var app = await TestApp.Start(configure: services => services.AddSingleton<ICommerceVerifier>(store));
        using var a = app.Client(app.A);
        Assert.Equal((true, false), await Status(a, "rc-history"));
        Assert.Equal((true, false), await Status(a, "store-order", "store"));
        Assert.Equal((false, false), await Status(a, "rc-history", "store"));
        Assert.Equal((false, false), await Status(a, "store-order", "revenuecat"));
        Assert.Equal((false, false), await Status(a, "rc-history", store: "play_store"));
        Assert.Equal((false, false), await Status(a, "rc-history", product: "zullo_boost_1"));
        using var b = app.Client(app.B);
        Assert.Equal((false, false), await Status(b, "rc-history"));
    }

    [Fact]
    public async Task AliasBackfillsExistingSpentLedgerWithoutRefillingCredits()
    {
        var store = new Store { Credits = new() { new("app_store:paid", "zullo_superlikes_5", 5, 0, "rc-paid") } };
        await using var app = await TestApp.Start(configure: services => services.AddSingleton<ICommerceVerifier>(store));
        await using (var db = app.Db())
        {
            db.CommerceAccounts.Add(new CommerceAccount { UserId = app.A, PurchasedSuperLikes = 0 });
            db.PurchaseGrants.Add(new PurchaseGrant { TransactionKey = "app_store:paid", UserId = app.A, ProductId = "zullo_superlikes_5", SuperLikes = 5 });
            await db.SaveChangesAsync();
        }
        using var client = app.Client(app.A);
        Assert.Equal((true, false), await Status(client, "rc-paid"));
        await using var check = app.Db();
        Assert.Equal(0, (await check.CommerceAccounts.SingleAsync(a => a.UserId == app.A)).PurchasedSuperLikes);
        Assert.Equal("rc-paid", (await check.PurchaseGrants.SingleAsync()).ProviderTransactionId);
    }

    [Fact]
    public async Task AuthenticatedRefundIsRecognizedByEitherIdAndNeverRegranted()
    {
        var store = new Store { Credits = new() { new("app_store:paid", "zullo_superlikes_5", 5, 0, "rc-paid", Refunded: true) } };
        await using var app = await TestApp.Start(configure: services => services.AddSingleton<ICommerceVerifier>(store));
        using var client = app.Client(app.A);
        Assert.Equal((false, true), await Status(client, "rc-paid"));
        Assert.Equal((false, true), await Status(client, "paid", "store"));
        store.Credits = new() { new("app_store:paid", "zullo_superlikes_5", 5, 0, "rc-paid") };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/commerce/sync", null)).StatusCode);
        await using var db = app.Db();
        Assert.Equal(0, (await db.CommerceAccounts.SingleAsync(a => a.UserId == app.A)).PurchasedSuperLikes);
        Assert.True((await db.PurchaseGrants.SingleAsync()).Refunded);
    }

    [Fact]
    public async Task ProviderRefundDebitsExistingCreditExactlyOnce()
    {
        var store = new Store { Credits = new() { new("play_store:order", "zullo_superlikes_5", 5, 0, "rc-order") } };
        await using var app = await TestApp.Start(configure: services => services.AddSingleton<ICommerceVerifier>(store));
        using var client = app.Client(app.A);
        await client.PostAsync("/commerce/sync", null);
        store.Credits = new() { new("play_store:order", "zullo_superlikes_5", 5, 0, "rc-order", Refunded: true) };
        await client.PostAsync("/commerce/sync", null);
        await client.PostAsync("/commerce/sync", null);
        await using var db = app.Db();
        Assert.Equal(0, (await db.CommerceAccounts.SingleAsync(a => a.UserId == app.A)).PurchasedSuperLikes);
        Assert.True((await db.PurchaseGrants.SingleAsync()).Refunded);
    }

    [Fact]
    public async Task AmbiguousAliasAndUnknownIdentifierKindFailClosed()
    {
        await using var app = await TestApp.Start();
        await using (var db = app.Db())
        {
            foreach (var id in new[] { "one", "two" })
                db.PurchaseGrants.Add(new PurchaseGrant { TransactionKey = "app_store:" + id, UserId = app.A, ProductId = "zullo_superlikes_5", ProviderTransactionId = "ambiguous" });
            await db.SaveChangesAsync();
        }
        using var client = app.Client(app.A);
        Assert.Equal((false, false), await Status(client, "ambiguous"));
        var response = await client.PostAsJsonAsync("/commerce/purchase-status", new { store = "app_store", transactionId = "one", productId = "zullo_superlikes_5", identifierKind = "unknown" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    sealed class Handler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
    sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderResponseAssociatesRcAndStoreIdsIncludingRefunds(bool refunded)
    {
        var user = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { request_date_ms = 1, subscriber = new { original_app_user_id = user.ToString(), entitlements = new { }, non_subscriptions = new Dictionary<string, object> { ["zullo_boost_1"] = new[] { new { id = "rc-id", store_transaction_id = "store-id", store = "play_store", is_sandbox = false, is_refunded = refunded } } } } });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Commerce:Enabled"] = "true", ["Commerce:RevenueCatSecretKey"] = "sk_test", ["Commerce:WebhookAuthorization"] = new string('x', 32) }).Build();
        var snapshot = await new RevenueCatVerifier(new HttpClient(new Handler(json)), config, new Env()).Fetch(user);
        var credit = Assert.Single(snapshot.Credits);
        Assert.Equal("play_store:store-id", credit.Key);
        Assert.Equal("rc-id", credit.ProviderTransactionId);
        Assert.Equal(refunded, credit.Refunded);
    }
    [Fact]
    public async Task ProviderIdWithoutStoreIdentityDoesNotCreateAnUnprovenGrant()
    {
        var user = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new { request_date_ms = 1, subscriber = new { original_app_user_id = user.ToString(), entitlements = new { }, non_subscriptions = new Dictionary<string, object> { ["zullo_boost_1"] = new[] { new { id = "rc-only", store = "app_store", is_sandbox = false } } } } });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Commerce:Enabled"] = "true", ["Commerce:RevenueCatSecretKey"] = "sk_test", ["Commerce:WebhookAuthorization"] = new string('x', 32) }).Build();
        var snapshot = await new RevenueCatVerifier(new HttpClient(new Handler(json)), config, new Env()).Fetch(user);
        Assert.Empty(snapshot.Credits);
    }
}
