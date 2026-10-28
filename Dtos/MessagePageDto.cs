namespace Zullo.Api.Dtos;

public class MessagePageDto
{
    public List<MessageDto> Messages { get; set; } = new();
    public bool HasMore { get; set; }
    public string? NextCursor { get; set; }
    public long? OutgoingReadThrough { get; set; }
    public DateTime? OutgoingReadAtUtc { get; set; }
    public string? LatestCursor { get; set; }
}
