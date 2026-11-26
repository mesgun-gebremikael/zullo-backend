namespace Zullo.Api.Models;

/// IDs-only outbox. Copy and current device tokens are resolved at delivery time.
public sealed class MatchNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MatchId { get; set; }
    public Guid RecipientUserId { get; set; }
    public Guid OtherUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAtUtc { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
}
