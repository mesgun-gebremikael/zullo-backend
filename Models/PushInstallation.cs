namespace Zullo.Api.Models;

/// Device-scoped ownership tombstone. Version survives logout to reject stale requests.
public sealed class PushInstallation
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string? Token { get; set; }
    public string? TokenHash { get; set; }
    public long Version { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
