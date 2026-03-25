namespace Zullo.Api.Dtos;

public class UpdateRadiusResponseDto
{
    // Enkel bekräftelse till frontend
    public string Message { get; set; } = "";

    // Den nya sparade radien
    public int MatchRadiusKm { get; set; }
}
