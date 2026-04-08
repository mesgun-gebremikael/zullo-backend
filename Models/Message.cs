namespace Zullo.Api.Models;

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }

    public string Text { get; set; } = "";

    //  Kopplar frontend message till backend message
    public string ClientMessageId { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAtUtc { get; set; }
}
