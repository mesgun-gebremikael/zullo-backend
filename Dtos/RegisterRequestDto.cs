using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos;

public class RegisterRequestDto
{
    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string Email { get; set; } = "";
    [Required]
    [MinLength(8)]
    [MaxLength(100)]
    [Zullo.Api.Validation.BcryptPassword]
    public string Password { get; set; } = "";
}
