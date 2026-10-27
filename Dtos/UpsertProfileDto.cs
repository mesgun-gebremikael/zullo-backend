using System.ComponentModel.DataAnnotations;

namespace Zullo.Api.Dtos;

/// Omitted fields are preserved. Explicit null clears HeightCm only.
public class UpsertProfileDto : IValidatableObject
{
    private readonly HashSet<string> _supplied = new();
    public bool Has(string property) => _supplied.Contains(property);

    private string? _DisplayName;
    [MaxLength(100)]
    public string? DisplayName
    {
        get => _DisplayName;
        set { _DisplayName = value; _supplied.Add(nameof(DisplayName)); }
    }

    private int? _Age;
    [Range(18, 100)]
    public int? Age
    {
        get => _Age;
        set { _Age = value; _supplied.Add(nameof(Age)); }
    }

    private string? _Gender;
    [MaxLength(30)]
    public string? Gender
    {
        get => _Gender;
        set { _Gender = value; _supplied.Add(nameof(Gender)); }
    }

    private string? _Bio;
    [MaxLength(1000)]
    public string? Bio
    {
        get => _Bio;
        set { _Bio = value; _supplied.Add(nameof(Bio)); }
    }

    private string? _Intention;
    [MaxLength(50)]
    public string? Intention
    {
        get => _Intention;
        set { _Intention = value; _supplied.Add(nameof(Intention)); }
    }

    private string? _Religion;
    [MaxLength(50)]
    public string? Religion
    {
        get => _Religion;
        set { _Religion = value; _supplied.Add(nameof(Religion)); }
    }

    private string? _Workout;
    [MaxLength(30)]
    public string? Workout
    {
        get => _Workout;
        set { _Workout = value; _supplied.Add(nameof(Workout)); }
    }

    private string? _Smoking;
    [MaxLength(30)]
    public string? Smoking
    {
        get => _Smoking;
        set { _Smoking = value; _supplied.Add(nameof(Smoking)); }
    }

    private string? _Pets;
    [MaxLength(30)]
    public string? Pets
    {
        get => _Pets;
        set { _Pets = value; _supplied.Add(nameof(Pets)); }
    }

    private int? _HeightCm;
    [Range(140, 260)]
    public int? HeightCm
    {
        get => _HeightCm;
        set { _HeightCm = value; _supplied.Add(nameof(HeightCm)); }
    }

    private string? _RelationshipHistory;
    [MaxLength(50)]
    public string? RelationshipHistory
    {
        get => _RelationshipHistory;
        set { _RelationshipHistory = value; _supplied.Add(nameof(RelationshipHistory)); }
    }

    private string? _ZodiacSign;
    [MaxLength(50)]
    public string? ZodiacSign
    {
        get => _ZodiacSign;
        set { _ZodiacSign = value; _supplied.Add(nameof(ZodiacSign)); }
    }

    private string? _Alcohol;
    [MaxLength(30)]
    public string? Alcohol
    {
        get => _Alcohol;
        set { _Alcohol = value; _supplied.Add(nameof(Alcohol)); }
    }

    private string? _Cannabis;
    [MaxLength(30)]
    public string? Cannabis
    {
        get => _Cannabis;
        set { _Cannabis = value; _supplied.Add(nameof(Cannabis)); }
    }

    private string? _ChildrenCount;
    [MaxLength(30)]
    public string? ChildrenCount
    {
        get => _ChildrenCount;
        set { _ChildrenCount = value; _supplied.Add(nameof(ChildrenCount)); }
    }

    private string? _WantChildren;
    [MaxLength(30)]
    public string? WantChildren
    {
        get => _WantChildren;
        set { _WantChildren = value; _supplied.Add(nameof(WantChildren)); }
    }

    private string? _WorkStatus;
    [MaxLength(30)]
    public string? WorkStatus
    {
        get => _WorkStatus;
        set { _WorkStatus = value; _supplied.Add(nameof(WorkStatus)); }
    }

    private string? _StudyPlace;
    [MaxLength(120)]
    public string? StudyPlace
    {
        get => _StudyPlace;
        set { _StudyPlace = value; _supplied.Add(nameof(StudyPlace)); }
    }

    private string? _StudySubject;
    [MaxLength(120)]
    public string? StudySubject
    {
        get => _StudySubject;
        set { _StudySubject = value; _supplied.Add(nameof(StudySubject)); }
    }

    private string? _WorkPlace;
    [MaxLength(120)]
    public string? WorkPlace
    {
        get => _WorkPlace;
        set { _WorkPlace = value; _supplied.Add(nameof(WorkPlace)); }
    }

    private string? _JobTitle;
    [MaxLength(120)]
    public string? JobTitle
    {
        get => _JobTitle;
        set { _JobTitle = value; _supplied.Add(nameof(JobTitle)); }
    }

    private string? _LivePlace;
    [MaxLength(120)]
    public string? LivePlace
    {
        get => _LivePlace;
        set { _LivePlace = value; _supplied.Add(nameof(LivePlace)); }
    }

    private string? _OriginPlace;
    [MaxLength(120)]
    public string? OriginPlace
    {
        get => _OriginPlace;
        set { _OriginPlace = value; _supplied.Add(nameof(OriginPlace)); }
    }

    private List<string>? _Interests;
    public List<string>? Interests
    {
        get => _Interests;
        set { _Interests = value; _supplied.Add(nameof(Interests)); }
    }

    private List<string>? _PhotoUrls;
    public List<string>? PhotoUrls
    {
        get => _PhotoUrls;
        set { _PhotoUrls = value; _supplied.Add(nameof(PhotoUrls)); }
    }

    private double? _Lat;
    [Range(-90.0, 90.0)]
    public double? Lat
    {
        get => _Lat;
        set { _Lat = value; _supplied.Add(nameof(Lat)); }
    }

    private double? _Lng;
    [Range(-180.0, 180.0)]
    public double? Lng
    {
        get => _Lng;
        set { _Lng = value; _supplied.Add(nameof(Lng)); }
    }

    private string? _CountryCode;
    [MaxLength(10)]
    public string? CountryCode
    {
        get => _CountryCode;
        set { _CountryCode = value; _supplied.Add(nameof(CountryCode)); }
    }

    private string? _InterestedIn;
    [MaxLength(30)]
    public string? InterestedIn
    {
        get => _InterestedIn;
        set { _InterestedIn = value; _supplied.Add(nameof(InterestedIn)); }
    }

    private int? _MatchRadiusKm;
    [Range(1, 300)]
    public int? MatchRadiusKm
    {
        get => _MatchRadiusKm;
        set { _MatchRadiusKm = value; _supplied.Add(nameof(MatchRadiusKm)); }
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        foreach (var name in _supplied)
        {
            var value = GetType().GetProperty(name)!.GetValue(this);
            if (value == null && name != nameof(HeightCm))
                yield return new ValidationResult("Use an empty string/list to clear this field; null is not allowed.", new[] { name });
        }
        if (Has(nameof(DisplayName)) && string.IsNullOrWhiteSpace(DisplayName))
            yield return new ValidationResult("DisplayName cannot be blank.", new[] { nameof(DisplayName) });
        if (Has(nameof(Gender)) && string.IsNullOrWhiteSpace(Gender))
            yield return new ValidationResult("Gender cannot be blank.", new[] { nameof(Gender) });
        if (InterestedIn != null && !new[] { "", "Alla", "Män", "Kvinnor" }.Contains(InterestedIn.Trim()))
            yield return new ValidationResult("Unsupported discovery preference.", new[] { nameof(InterestedIn) });
        if (Has(nameof(Lat)) != Has(nameof(Lng)))
            yield return new ValidationResult("Lat and Lng must be supplied together.", new[] { nameof(Lat), nameof(Lng) });
        if (Interests != null && (Interests.Count > 30 || Interests.Any(x => x == null || x.Length > 50)))
            yield return new ValidationResult("At most 30 non-null interests of 50 characters are allowed.", new[] { nameof(Interests) });
        if (PhotoUrls != null && (PhotoUrls.Count > 6 || PhotoUrls.Any(x =>
            x == null || x.Length > 2048 || !Uri.TryCreate(x.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)))
            yield return new ValidationResult("At most 6 HTTPS photo URLs are allowed.", new[] { nameof(PhotoUrls) });
    }
}
