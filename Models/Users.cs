using System.Text.Json.Serialization;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Zullo.Api.Models
{
    public class User
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public bool IsPaused { get; set; }
        public bool IsClosed { get; set; }
        public DateTime? ClosedAtUtc { get; set; }

        // Auth identifiers (one or more can exist)
        public string? Email { get; set; }
        public string? PhoneNumber { get; set; }
        public string PasswordHash { get; set; } = "";
        public string? GoogleSubject { get; set; } // unique id from Google

        public string? DeviceToken { get; set; } // Firebase push token

        public bool IsVerified { get; set; } = false; // verified login method
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        // Tinder-like free likes
        public int LikesRemaining { get; set; } = 50;

        public bool DiscoveryAgeConfigured { get; set; }
        public bool ExpandDistance { get; set; }
        public bool ExpandAge { get; set; }
        public int DiscoveryMinAge { get; set; } = 18;
        public int DiscoveryMaxAge { get; set; } = 99;
        public int MatchRadiusKm { get; set; } = 50;

        public DateTime LikesResetAtUtc { get; set; } = DateTime.UnixEpoch;

        // Navigation

       [JsonIgnore]
       public Profile? Profile { get; set; }

    }
}
