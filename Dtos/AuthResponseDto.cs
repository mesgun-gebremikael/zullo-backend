namespace Zullo.Api.Dtos;

public class AuthResponseDto
{
    // JWT som frontend sparar och skickar med i requests
    public string Token { get; set; } = "";

    // Inloggad användares id
    public Guid UserId { get; set; }

    public string? Email { get; set; }

    // Används just nu bara efter register
    public string? Message { get; set; }
}