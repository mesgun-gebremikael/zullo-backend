namespace Zullo.Api.Dtos;

public class SwipeFeedResponseDto
{
    // Användarens nuvarande matchradie
    public int RadiusKm { get; set; }

    public int MinAge { get; set; }
    public int MaxAge { get; set; }

    // Behåll dessa just nu för kompatibilitet/debug
    public int TotalProfilesInDb { get; set; }
    public int VisibleProfilesInDb { get; set; }
    public int CandidateCount { get; set; }
    public int ResultCount { get; set; }

    public double MyLat { get; set; }
    public double MyLng { get; set; }

    public List<SwipeProfileDto> Profiles { get; set; } = new();
}