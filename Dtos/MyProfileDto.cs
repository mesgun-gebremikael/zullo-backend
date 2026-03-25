using Zullo.Api.Models;

namespace Zullo.Api.Dtos;

public class MyProfileDto
{
    // Min användares profil
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = "";
    public int Age { get; set; }
    public string Gender { get; set; } = "";
    public string Bio { get; set; } = "";

    public IntentionType Intention { get; set; }
    public ReligionType Religion { get; set; }

    public TriState Workout { get; set; }
    public TriState Smoking { get; set; }
    public PetsType Pets { get; set; }

    // Listor sparas redan som jsonb i Postgres
    public List<string> Interests { get; set; } = new();
    public List<string> PhotoUrls { get; set; } = new();

    public double Lat { get; set; }
    public double Lng { get; set; }
    public string CountryCode { get; set; } = "";

    public bool IsVisible { get; set; }
}
