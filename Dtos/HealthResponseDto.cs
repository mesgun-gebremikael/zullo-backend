namespace Zullo.Api.Dtos;

public class HealthResponseDto
{
    // Enkel status för att visa att API:t lever
    public string Status { get; set; } = "";

    // Miljön appen kör i, t.ex. Development eller Production
    public string Environment { get; set; } = "";

    // UTC-tid för snabb kontroll
    public DateTime UtcNow { get; set; }
}
