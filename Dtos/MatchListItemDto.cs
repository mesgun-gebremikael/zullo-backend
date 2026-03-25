namespace Zullo.Api.Dtos;

public class MatchListItemDto
{
    // Den andra användarens userId
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = "";
    public int Age { get; set; }

    // Första profilbilden som visas i matches-listan
    public string PhotoUrl { get; set; } = "";

    // Senaste meddelandet i tråden
    public string? LastMessageText { get; set; }

    // När senaste meddelandet skickades
    public DateTime? LastMessageAtUtc { get; set; }

    // Fallback om ingen message finns ännu
    public DateTime? MatchCreatedAtUtc { get; set; }

    // Visar unread-dot i frontend
    public bool HasUnread { get; set; }
}
