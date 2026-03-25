namespace Zullo.Api.Dtos;

public class MarkReadResponseDto
{
    // Hur många meddelanden som markerades som lästa
    public int Updated { get; set; }

    // När de markerades som lästa
    public DateTime? ReadAtUtc { get; set; }
}
