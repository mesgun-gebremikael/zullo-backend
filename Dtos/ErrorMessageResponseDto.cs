namespace Zullo.Api.Dtos;

public class ErrorMessageResponseDto
{
    // Enkel standardrespons för fel med bara ett message-fält
    public string Message { get; set; } = "";
}