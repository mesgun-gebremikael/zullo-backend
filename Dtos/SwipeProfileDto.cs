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