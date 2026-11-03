namespace Zullo.Api.Models;

public class Block
{
    public Guid Id { get; set; }

    // vem blockerar
    public Guid FromUserId { get; set; }

    // vem blir blockerad
    public Guid BlockedUserId { get; set; }

    public string SnapshotDisplayName { get; set; } = "";
    public int? SnapshotAge { get; set; }
    public string SnapshotPhotoUrl { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    
}