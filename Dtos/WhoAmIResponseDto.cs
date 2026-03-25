namespace Zullo.Api.Dtos;

public class ClaimItemDto
{
    // En claim från JWT/token
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
}

public class WhoAmIResponseDto
{
    public string? NameIdentifier { get; set; }
    public string? Email { get; set; }

    // Alla claims som finns i token
    public List<ClaimItemDto> AllClaims { get; set; } = new();
}
