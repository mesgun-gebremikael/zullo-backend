namespace Zullo.Api.Dtos;

public class ApiErrorResponseDto
{
    // Kort felmeddelande till klienten
    public string Message { get; set; } = "";

    // Valfri teknisk detalj i development
    public string? Detail { get; set; }
}
