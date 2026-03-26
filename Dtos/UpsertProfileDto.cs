using System.ComponentModel.DataAnnotations;
using Zullo.Api.Models;

namespace Zullo.Api.Dtos;

public class UpsertProfileDto
{
    [Required]
    [MaxLength(100)]
    public string DisplayName { get; set; } = "";

    [Range(18, 100)]
    public int Age { get; set; }

    [Required]
    [MaxLength(30)]
    public string Gender { get; set; } = "";

    [MaxLength(1000)]
    public string Bio { get; set; } = "";

    public IntentionType Intention { get; set; } = IntentionType.Relationship;
    public ReligionType Religion { get; set; } = ReligionType.Private;

    public TriState Workout { get; set; } = TriState.Sometimes;
    public TriState Smoking { get; set; } = TriState.No;
    public PetsType Pets { get; set; } = PetsType.Want;

    public List<string> Interests { get; set; } = new();
    public List<string> PhotoUrls { get; set; } = new();

    public double Lat { get; set; }
    public double Lng { get; set; }

    [MaxLength(10)]
    public string CountryCode { get; set; } = "";
}

