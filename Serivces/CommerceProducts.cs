namespace Zullo.Api.Services;

/// Store identifiers are configuration, never client-granted entitlements.
public static class CommerceProducts
{
    public const string PremiumMonthly = "zullo_premium_monthly";
    public const string PremiumAnnual = "zullo_premium_annual";
    public const string PremiumGoogleSubscription = "zullo_premium";
    public const string LegacyActivityMonthly = "zullo_hide_activity_monthly";
    public static bool IsPremium(string product) => product.Split(':')[0] is
        PremiumMonthly or PremiumAnnual or PremiumGoogleSubscription;
    public static bool IsLegacyActivity(string product) => product.Split(':')[0] == LegacyActivityMonthly;
    public static readonly string[] NewSubscriptionPlans = [PremiumMonthly, PremiumAnnual];
}
