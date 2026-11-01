namespace Zullo.Api.Models;

// Durable follow-up for external assets. Existing unsigned uploads have no
// ownership receipt: they require operator review, never blind URL deletion.
public class AccountRemovalRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FormerUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<string> PhotoUrls { get; set; } = new();
    public DateTime? LastCleanupCheckUtc { get; set; }
    public bool ExternalAssetsReviewed { get; set; }
}
