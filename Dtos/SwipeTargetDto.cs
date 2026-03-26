using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos;

public class SwipeTargetDto
{
    [Required]
    public Guid TargetUserId { get; set; }
}
