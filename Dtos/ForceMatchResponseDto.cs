namespace Zullo.Api.Dtos;

public class ForceMatchResponseDto
{
    // Enkel bekräftelse för test/dev
    public string Message { get; set; } = "";

    // Den inloggade användaren
    public Guid MeId { get; set; }

    // Den användare vi force-matchade mot
    public Guid TargetUserId { get; set; }
}
