using System.Text.Json.Serialization;

namespace Zullo.Api.Models;

public enum IntentionType { Date, Relationship, Marriage }
public enum ReligionType { Christian, Muslim, Atheist, Private }
public enum TriState { Yes, Sometimes, No }
public enum PetsType { Have, Want, No }

public class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = "";
    public int Age { get; set; } // must be >= 18
    public string Gender { get; set; } = ""; // keep simple v1

    public string Bio { get; set; } = "";

    public IntentionType Intention { get; set; } = IntentionType.Relationship;
    public ReligionType Religion { get; set; } = ReligionType.Private;

    // Tinder-like lifestyle
    public TriState Workout { get; set; } = TriState.Sometimes;
    public TriState Smoking { get; set; } = TriState.No;
    public PetsType Pets { get; set; } = PetsType.Want;

    // Interests as simple list (we'll store as JSON/text for v1)
    public List<string> Interests { get; set; } = new();

    // Photos URLs (min 2 to be visible)
    public List<string> PhotoUrls { get; set; } = new();

    // Location for distance matching
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string CountryCode { get; set; } = "";

    public bool IsVisible { get; set; } = false;

    [JsonIgnore]
    public User? User { get; set; }
}
