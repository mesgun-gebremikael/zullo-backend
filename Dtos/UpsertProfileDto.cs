using Zullo.Api.Models;

namespace Zullo.Api.Dtos;

public class UpsertProfileDto
{
    public string DisplayName { get; set; } = "";
    public int Age { get; set; }
    public string Gender { get; set; } = "";
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
    public string CountryCode { get; set; } = "";
}

