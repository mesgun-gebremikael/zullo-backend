using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos
{
    public class UpdateRadiusDto
    {
        [Range(1, 300)]
        public int MatchRadiusKm { get; set; }
    }
}
