namespace Zullo.Api.Dtos;

public class UpsertProfileResponseDto
{
    // Bekräftelse att profilen sparades
    public string Message { get; set; } = "";

    // True när profilen har minst 2 bilder och kan visas i feeden
    public bool IsVisible { get; set; }
}
