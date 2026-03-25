namespace Zullo.Api.Dtos;

public class MessageDto
{
    // Unikt id för meddelandet
    public Guid Id { get; set; }

    // Vem skickade
    public Guid FromUserId { get; set; }

    // Vem tog emot
    public Guid ToUserId { get; set; }

    // Själva texten
    public string Text { get; set; } = "";

    // När meddelandet skapades
    public DateTime CreatedAtUtc { get; set; }

    // När mottagaren läste meddelandet
    public DateTime? ReadAtUtc { get; set; }
}
