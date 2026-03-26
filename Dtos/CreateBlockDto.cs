using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos;

public class CreateBlockDto
{
    [Required]
    public Guid BlockedUserId { get; set; }
}