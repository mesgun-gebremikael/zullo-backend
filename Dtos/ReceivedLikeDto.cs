namespace Zullo.Api.Dtos;

public class ReceivedLikeDto
{
    // Den användare som har gillat mig
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = "";
    public int Age { get; set; }

    // Första bilden för preview i UI
    public string PhotoUrl { get; set; } = "";
}
