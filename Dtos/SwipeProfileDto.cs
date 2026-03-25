using Zullo.Api.Models;

namespace Zullo.Api.Dtos;

public class SwipeProfileDto
{
    // Andra användarens id
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = "";
    public int Age { get; set; }
    public string Bio { get; set; } = "";

    public IntentionType Intention { get; set; }
    public ReligionType Religion { get; set; }

    public TriState Workout { get; set; }
    public TriState Smoking { get; set; }
    public PetsType Pets { get; set; }

    // Intressen och alla bilder behövs i swipe-kortet
    public List<string> Interests { get; set; } = new();
    public List<string> PhotoUrls { get; set; } = new();

    // Första bild om frontend vill använda en snabb preview
    public string PhotoUrl { get; set; } = "";

    public string CountryCode { get; set; } = "";

    // Avståndet som visas i feeden
    public double DistanceKm { get; set; }
}