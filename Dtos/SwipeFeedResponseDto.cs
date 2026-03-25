namespace Zullo.Api.Dtos;

public class SwipeFeedResponseDto
{
    // Användarens nuvarande matchradie
    public int RadiusKm { get; set; }

    // Profilerna som visas i swipe-feeden
    public List<SwipeProfileDto> Profiles { get; set; } = new();
}