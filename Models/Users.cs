using System.Text.Json.Serialization;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Zullo.Api.Models
{
    public class User
    {
        public Guid Id { get; set; } = Guid.NewGuid();

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

        public int MatchRadiusKm { get; set; } = 50;

        public DateTime LikesResetAtUtc { get; set; } = DateTime.UnixEpoch;

        // Navigation

       [JsonIgnore]
       public Profile? Profile { get; set; }

    }
}
