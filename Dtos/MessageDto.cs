namespace Zullo.Api.Dtos;

public class MessageDto
{
    //  Unikt ID från databasen (backend skapar detta)
    public Guid Id { get; set; }

    //  Vem skickade meddelandet
    public Guid FromUserId { get; set; }

    //  Vem tog emot meddelandet
    public Guid ToUserId { get; set; }

    //  Själva texten som skickas
    public string Text { get; set; } = "";

    //  När meddelandet skapades (UTC från backend)
    public DateTime CreatedAtUtc { get; set; }

    //  När mottagaren läste meddelandet (null = inte läst ännu)
    public DateTime? ReadAtUtc { get; set; }

    //  SUPER VIKTIGT
    // Detta ID kommer från frontend (Flutter)
    // Används för att matcha optimistic message → riktig message
    public string ClientMessageId { get; set; } = "";
}


