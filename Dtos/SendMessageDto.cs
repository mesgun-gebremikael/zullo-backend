namespace Zullo.Api.Dtos;

public class SendMessageDto
{
    public Guid ToUserId { get; set; }
    public string Text { get; set; } = "";
}