namespace Zullo.Api.Models;

/// No User or bearer identity exists until this email ownership proof succeeds.
/// Password and code hashes are erased when the challenge is consumed.
public class PendingEmailRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChallengeId { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime NextSendAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTime SendWindowStartedAtUtc { get; set; }
    public int SendCount { get; set; }
    public DateTime? DeliveryConfirmedAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}
