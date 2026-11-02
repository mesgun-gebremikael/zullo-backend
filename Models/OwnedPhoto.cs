namespace Zullo.Api.Models;

public static class PhotoStates
{
    public const string Uploading = "uploading", Ready = "ready", Attached = "attached",
        Pending = "delete_pending", Deleted = "deleted", Review = "legacy_review";
}

// No cascade from User: cleanup must survive account closure or later account purging.
public class OwnedPhoto
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string State { get; set; } = PhotoStates.Uploading;
    public string CloudName { get; set; } = "";
    public string? PublicId { get; set; }
    public string? ProviderAssetId { get; set; }
    public string? Url { get; set; }
    public List<string> Aliases { get; set; } = new();
    public string FileHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UploadFinishedUtc { get; set; }
    public Guid? UploadLease { get; set; }
    public DateTime? UploadLeaseUntilUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;
    public Guid? CleanupLease { get; set; }
    public DateTime? CleanupLeaseUntilUtc { get; set; }
    public int UploadAttempts { get; set; }
    public int DeleteAttempts { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public DateTime? LegacyApprovedAtUtc { get; set; }
    public string? LegacyApprovalDigest { get; set; }
}
