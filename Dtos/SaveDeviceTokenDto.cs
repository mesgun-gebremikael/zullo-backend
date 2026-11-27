using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos
{
    public class SaveDeviceTokenDto
    {
        [Required]
        [MaxLength(4096)]
        public string Token { get; set; } = "";
        public Guid? InstallationId { get; set; }
        public long? Version { get; set; }
    }
}

namespace Zullo.Api.Dtos
{
    public sealed class ClearDeviceTokenDto
    {
        public Guid? InstallationId { get; set; }
        public long? Version { get; set; }
    }
}
