using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos
{
    public class SaveDeviceTokenDto
    {
        [Required]
        public string Token { get; set; } = "";
    }
}
