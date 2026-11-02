namespace Zullo.Api.Models;

// Private preparation data. It never creates a discoverable Profile.
public class OnboardingDraft
{
    public Guid UserId { get; set; }
    public long Revision { get; set; }
    public int Step { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
