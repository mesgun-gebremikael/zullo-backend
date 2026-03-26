using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos
{
    public class UpdateRadiusDto
    {
        [Range(1, 200)]
        public int MatchRadiusKm { get; set; }
    }
}
