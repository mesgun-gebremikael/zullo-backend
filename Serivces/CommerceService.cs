using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Services;

public record VerifiedCredit(string Key, string ProductId, int SuperLikes, int Boosts, string? ProviderTransactionId = null, bool Refunded = false);
public record VerifiedCommerce(long SnapshotMs, DateTime? PremiumUntil, DateTime? PeriodStart, List<VerifiedCredit> Credits, DateTime? ActivityPrivacyUntil = null);
public record CommerceState(bool CheckoutEnabled, bool Premium, DateTime? PremiumUntilUtc, int SuperLikes,
    int Boosts, DateTime? BoostUntilUtc, bool HideActivity, bool Incognito, bool CanHideActivity = false, DateTime? ActivityPrivacyUntilUtc = null, int IncludedSuperLikes = 0, int PurchasedSuperLikes = 0,
    int IncludedBoosts = 0, int PurchasedBoosts = 0, DateTime? NextSuperLikeRefreshUtc = null, DateTime? NextBoostRefreshUtc = null);

public interface ICommerceVerifier
{
    bool Enabled { get; }
    Task<VerifiedCommerce> Fetch(Guid user, CancellationToken cancellation = default);
}

// The phone never grants entitlements or balances: only the authenticated provider API does.
public class RevenueCatVerifier(HttpClient client, IConfiguration config, IHostEnvironment environment) : ICommerceVerifier
{
    public bool Enabled => config.GetValue<bool>("Commerce:Enabled") &&
        (config["Commerce:RevenueCatSecretKey"] ?? "").StartsWith("sk_", StringComparison.Ordinal) &&
        (config["Commerce:WebhookAuthorization"] ?? "").Length >= 32;
    public async Task<VerifiedCommerce> Fetch(Guid user, CancellationToken cancellation = default)
    {
        if (!Enabled) throw new InvalidOperationException("Checkout is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.revenuecat.com/v1/subscribers/{user}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Commerce:RevenueCatSecretKey"]);
        using var response = await client.SendAsync(request, cancellation);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new VerifiedCommerce(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, null, new());
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var root = json.RootElement;
        var subscriber = root.GetProperty("subscriber");
        if (subscriber.GetProperty("original_app_user_id").GetString() != user.ToString())
            throw new InvalidOperationException("Purchase account does not match the signed-in account.");
        var sandboxAllowed = !environment.IsProduction() && config.GetValue<bool>("Commerce:AllowSandbox");
        (DateTime? Expiry, DateTime? Period) Subscription(string entitlement, Func<string, bool> knownProduct)
        {
            if (!subscriber.TryGetProperty("entitlements", out var entitlements) ||
                !entitlements.TryGetProperty(entitlement, out var benefit)) return (null, null);
            var product = benefit.GetProperty("product_identifier").GetString()!;
            if (!knownProduct(product) ||
                !subscriber.TryGetProperty("subscriptions", out var subscriptions) ||
                !subscriptions.TryGetProperty(product, out var subscription) ||
                !subscription.TryGetProperty("store", out var store) ||
                store.GetString() is not ("app_store" or "play_store") ||
                (subscription.GetProperty("is_sandbox").GetBoolean() && !sandboxAllowed) ||
                Date(subscription, "refunded_at") != null) return (null, null);
            var expiry = Date(benefit, "expires_date");
            var grace = Date(subscription, "grace_period_expires_date") ?? Date(benefit, "grace_period_expires_date");
            if (grace > expiry) expiry = grace;
            return (expiry, Date(benefit, "purchase_date"));
        }
        var (expires, period) = Subscription("premium", CommerceProducts.IsPremium);
        var (activityExpiry, _) = Subscription("activity_privacy", CommerceProducts.IsLegacyActivity);
        var credits = new List<VerifiedCredit>();
        if (subscriber.TryGetProperty("non_subscriptions", out var purchases))
        foreach (var product in purchases.EnumerateObject())
        {
            var amounts = ProductAmounts(product.Name);
            if (amounts == (0, 0)) continue;
            foreach (var purchase in product.Value.EnumerateArray())
            {
                if (purchase.GetProperty("is_sandbox").GetBoolean() && !sandboxAllowed) continue;
                var isRefunded = purchase.TryGetProperty("is_refunded", out var refunded) && refunded.GetBoolean();
                // Older v1 responses expose only a RevenueCat ID. Wait for the signed webhook
                // rather than confusing that ID with a store transaction and double-granting.
                if (!purchase.TryGetProperty("store_transaction_id", out var transaction)) continue;
                var id = transaction.ToString();
                var store = purchase.GetProperty("store").GetString()!.ToLowerInvariant();
                if (store is not ("app_store" or "play_store")) continue;
                if (string.IsNullOrWhiteSpace(id) || id.Length > 180) throw new InvalidOperationException("Invalid store transaction.");
                var providerId = purchase.TryGetProperty("id", out var providerTransaction) ? providerTransaction.ToString() : null;
                if (providerId?.Length > 180) throw new InvalidOperationException("Invalid provider transaction.");
                credits.Add(new VerifiedCredit($"{store}:{id}", product.Name, amounts.Item1, amounts.Item2,
                    string.IsNullOrWhiteSpace(providerId) ? null : providerId, isRefunded));
            }
        }
        return new VerifiedCommerce(root.GetProperty("request_date_ms").GetInt64(), expires, period, credits, activityExpiry);
    }
    private static DateTime? Date(JsonElement value, string key) => value.TryGetProperty(key, out var date) && date.ValueKind == JsonValueKind.String
        ? DateTime.Parse(date.GetString()!, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal) : null;
    public static (int, int) ProductAmounts(string product) => product switch
    {
        "zullo_superlikes_5" => (5, 0), "zullo_boost_1" => (0, 1), "zullo_boosts_5" => (0, 5), _ => (0, 0)
    };
}

public class CommerceService(AppDbContext db, ICommerceVerifier verifier)
{
    public static bool Premium(CommerceAccount? account) => account?.PremiumUntilUtc > DateTime.UtcNow;
    public static bool CanHideActivity(CommerceAccount? account) => Premium(account) || account?.ActivityPrivacyUntilUtc > DateTime.UtcNow;
    public async Task<IDbContextTransaction> Begin(Guid user)
    {
        var transaction = await db.Database.BeginTransactionAsync();
        await Lock(user);
        return transaction;
    }
    public async Task Lock(Guid user)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"User\" WHERE \"Id\" = {user} FOR UPDATE");
    }
    public async Task<CommerceAccount> Account(Guid user)
    {
        var tracked = db.ChangeTracker.Entries<CommerceAccount>().FirstOrDefault(e => e.Entity.UserId == user && e.State != EntityState.Added);
        if (tracked != null) await tracked.ReloadAsync();
        var account = await db.CommerceAccounts.SingleOrDefaultAsync(a => a.UserId == user);
        if (account == null) { account = new CommerceAccount { UserId = user }; db.CommerceAccounts.Add(account); }
        RefreshAllowances(account);
        return account;
    }
    // Annual and monthly subscribers receive the same monthly Boost allowance.
    // Derive each boundary from the verified anchor, avoiding Jan31→Feb28→Mar28 drift.
    public static (DateTime Start, DateTime Next)? BoostWindow(CommerceAccount account, DateTime now)
    {
        if (account.PremiumPeriodStartUtc is not { } anchor || anchor > now) return null;
        var months = (now.Year - anchor.Year) * 12 + now.Month - anchor.Month;
        if (anchor.AddMonths(months) > now) months--;
        return (anchor.AddMonths(months), anchor.AddMonths(months + 1));
    }

    public static void RefreshAllowances(CommerceAccount account, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        if (account.PremiumUntilUtc <= now || account.PremiumUntilUtc == null) return;
        var window = BoostWindow(account, now);
        if (window != null && account.BoostAllowancePeriodUtc != window.Value.Start)
        {
            account.IncludedBoosts = 1;
            account.BoostAllowancePeriodUtc = window.Value.Start;
        }
        if (account.NextSuperLikeRefreshUtc <= now)
        {
            account.IncludedSuperLikes = 3;
            account.NextSuperLikeRefreshUtc = now.AddDays(7);
        }
    }
    public async Task RefreshBenefitsIfSubscribed(Guid user)
    {
        var account = await db.CommerceAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == user);
        if (CanHideActivity(account)) await State(user);
    }

    public async Task<CommerceState> State(Guid user, bool force = false, CancellationToken cancellation = default)
    {
        var existing = await db.CommerceAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == user, cancellation);
        VerifiedCommerce? verified = null;
        var checkoutHealthy = true;
        if (verifier.Enabled && (force || existing?.LastVerifiedUtc == null || existing.LastVerifiedUtc < DateTime.UtcNow.AddMinutes(-2)))
        {
            try { verified = await verifier.Fetch(user, cancellation); }
            catch (Exception ex) when (!force && ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            { checkoutHealthy = false; /* Preserve previously verified rights; never fabricate a purchase. */ }
        }
        await using var transaction = await Begin(user);
        var account = await Account(user);
        if (verified != null && verified.SnapshotMs >= account.ProviderSnapshotMs)
        {
            account.PremiumUntilUtc = verified.PremiumUntil;
            account.ActivityPrivacyUntilUtc = verified.ActivityPrivacyUntil;
            account.PremiumPeriodStartUtc = verified.PeriodStart;
            account.ProviderSnapshotMs = verified.SnapshotMs;
            account.LastVerifiedUtc = DateTime.UtcNow;
            foreach (var credit in verified.Credits.DistinctBy(c => c.Key))
            {
                var existingGrant = await db.PurchaseGrants.SingleOrDefaultAsync(g => g.TransactionKey == credit.Key, cancellation);
                if (existingGrant != null)
                {
                    // Backfill an authenticated alias without granting again, reviving a
                    // refund or moving a transaction between accounts/products.
                    if (existingGrant.UserId == user && existingGrant.ProductId == credit.ProductId &&
                        existingGrant.ProviderTransactionId == null)
                        existingGrant.ProviderTransactionId = credit.ProviderTransactionId;
                    if (existingGrant.UserId == user && existingGrant.ProductId == credit.ProductId && credit.Refunded && !existingGrant.Refunded)
                    {
                        account.PurchasedSuperLikes -= existingGrant.SuperLikes;
                        account.PurchasedBoosts -= existingGrant.Boosts;
                        existingGrant.Refunded = true;
                    }
                    continue;
                }
                db.PurchaseGrants.Add(new PurchaseGrant { TransactionKey = credit.Key, UserId = user, ProductId = credit.ProductId,
                    ProviderTransactionId = credit.ProviderTransactionId, Refunded = credit.Refunded, SuperLikes = credit.SuperLikes, Boosts = credit.Boosts });
                if (!credit.Refunded)
                {
                    account.PurchasedSuperLikes = checked(account.PurchasedSuperLikes + credit.SuperLikes);
                    account.PurchasedBoosts = checked(account.PurchasedBoosts + credit.Boosts);
                }
            }
            RefreshAllowances(account);
        }
        await db.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        return ToState(account) with { CheckoutEnabled = verifier.Enabled && checkoutHealthy };
    }
    public async Task ApplyCredit(Guid user, VerifiedCredit credit, bool refund, CancellationToken cancellation)
    {
        await using var transaction = await Begin(user);
        var account = await Account(user);
        var grant = await db.PurchaseGrants.SingleOrDefaultAsync(g => g.TransactionKey == credit.Key, cancellation);
        if (grant != null && grant.UserId != user) return;
        if (grant == null)
        {
            grant = new PurchaseGrant { TransactionKey = credit.Key, UserId = user, ProductId = credit.ProductId,
                SuperLikes = credit.SuperLikes, Boosts = credit.Boosts, Refunded = refund };
            db.PurchaseGrants.Add(grant);
            if (!refund)
            {
                account.PurchasedSuperLikes = checked(account.PurchasedSuperLikes + credit.SuperLikes);
                account.PurchasedBoosts = checked(account.PurchasedBoosts + credit.Boosts);
            }
        }
        else if (refund && !grant.Refunded)
        {
            account.PurchasedSuperLikes -= grant.SuperLikes;
            account.PurchasedBoosts -= grant.Boosts;
            grant.Refunded = true;
        }
        await db.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
    }

    public CommerceState ToState(CommerceAccount account)
    {
        var premium = Premium(account);
        var includedSuperLikes = premium ? account.IncludedSuperLikes : 0;
        var includedBoosts = premium ? account.IncludedBoosts : 0;
        var purchasedSuperLikes = Math.Max(0, account.PurchasedSuperLikes);
        var purchasedBoosts = Math.Max(0, account.PurchasedBoosts);
        return new(verifier.Enabled, premium, account.PremiumUntilUtc,
            purchasedSuperLikes + includedSuperLikes, purchasedBoosts + includedBoosts,
            account.BoostUntilUtc > DateTime.UtcNow ? account.BoostUntilUtc : null,
            CanHideActivity(account) && account.HideActivity, premium && account.Incognito,
            CanHideActivity(account), account.ActivityPrivacyUntilUtc,
            includedSuperLikes, purchasedSuperLikes, includedBoosts, purchasedBoosts,
            premium ? account.NextSuperLikeRefreshUtc : null,
            premium ? BoostWindow(account, DateTime.UtcNow)?.Next : null);
    }
    public static bool ConsumeSuperLike(CommerceAccount account)
    {
        RefreshAllowances(account);
        if (Premium(account) && account.IncludedSuperLikes > 0) { account.IncludedSuperLikes--; return true; }
        if (account.PurchasedSuperLikes > 0) { account.PurchasedSuperLikes--; return true; }
        return false;
    }
    public static bool ConsumeBoost(CommerceAccount account)
    {
        RefreshAllowances(account);
        if (Premium(account) && account.IncludedBoosts > 0) { account.IncludedBoosts--; return true; }
        if (account.PurchasedBoosts > 0) { account.PurchasedBoosts--; return true; }
        return false;
    }
}
