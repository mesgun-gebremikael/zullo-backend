using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos;

public class SendMessageDto
{
    [Required]
    public Guid ToUserId { get; set; }

    [Required]
    [MaxLength(2000)]
    public string Text { get; set; } = "";

    [Required]
    [MaxLength(100)]
    public string ClientMessageId { get; set; } = "";
}
