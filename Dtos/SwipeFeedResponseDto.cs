namespace Zullo.Api.Dtos;

public class SwipeFeedResponseDto
{
    public bool IsPaused { get; set; }
    // Användarens nuvarande matchradie
    public int RadiusKm { get; set; }

    // Profilerna som visas i swipe-feeden
    public List<SwipeProfileDto> Profiles { get; set; } = new();
}