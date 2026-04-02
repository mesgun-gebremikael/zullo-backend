using Zullo.Api.Models;

namespace Zullo.Api.Dtos;

public class SwipeProfileDto
{
    // Andra användarens id
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = "";
    public int Age { get; set; }
    public string Bio { get; set; } = "";

    public string Intention { get; set; } = "";
    public string Religion { get; set; } = "";

    public string Workout { get; set; } = "";
    public string Smoking { get; set; } = "";
    public string Pets { get; set; } = "";

    public int? HeightCm { get; set; }

    public string RelationshipHistory { get; set; } = "";
    public string ZodiacSign { get; set; } = "";

    public string Alcohol { get; set; } = "";
    public string Cannabis { get; set; } = "";

    public string ChildrenCount { get; set; } = "";
    public string WantChildren { get; set; } = "";

    public string WorkStatus { get; set; } = "";
    public string StudyPlace { get; set; } = "";
    public string StudySubject { get; set; } = "";
    public string WorkPlace { get; set; } = "";
    public string JobTitle { get; set; } = "";

    public string LivePlace { get; set; } = "";
    public string OriginPlace { get; set; } = "";

    // Intressen och alla bilder behövs i swipe-kortet
    public List<string> Interests { get; set; } = new();

    //Feed ska i praktiken visa profiler som har minst 2 bilder
    public List<string> PhotoUrls { get; set; } = new();

    // Första bild om frontend vill använda en snabb preview
    public string PhotoUrl { get; set; } = "";

    public string CountryCode { get; set; } = "";

    // Avståndet som visas i feeden
    public double DistanceKm { get; set; }
}