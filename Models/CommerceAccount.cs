namespace Zullo.Api.Models;

public class CommerceAccount
{
    public Guid UserId { get; set; }
    public DateTime? ActivityPrivacyUntilUtc { get; set; }
    public DateTime? PremiumUntilUtc { get; set; }
    public DateTime? PremiumPeriodStartUtc { get; set; }
    public DateTime? BoostAllowancePeriodUtc { get; set; }
    public DateTime NextSuperLikeRefreshUtc { get; set; }
    public int IncludedSuperLikes { get; set; }
    public int IncludedBoosts { get; set; }
    public int PurchasedSuperLikes { get; set; }
    public int PurchasedBoosts { get; set; }
    public DateTime? BoostUntilUtc { get; set; }
    public bool HideActivity { get; set; }
    public bool Incognito { get; set; }
    public DateTime? LastActivityUtc { get; set; }
    public long ProviderSnapshotMs { get; set; }
    public DateTime? LastVerifiedUtc { get; set; }
}

public class PurchaseGrant
{
    public string TransactionKey { get; set; } = "";
    public Guid UserId { get; set; }
    public string ProductId { get; set; } = "";
    public int SuperLikes { get; set; }
    public int Boosts { get; set; }
    public bool Refunded { get; set; }
}
