using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Models;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers
{
    [ApiController]
    [Route("me")]
    [Authorize] // ✅ kräver JWT
    public class MeController : ControllerBase
    {
        private readonly AppDbContext _db;

        public MeController(AppDbContext db)
        {
            _db = db;
        }

        private static void MapUpsertProfileDtoToProfile(UpsertProfileDto dto, Profile profile)
        {
            if (dto.Has(nameof(dto.DisplayName))) profile.DisplayName = dto.DisplayName!.Trim();
            if (dto.Has(nameof(dto.Age))) profile.Age = dto.Age!.Value;
            if (dto.Has(nameof(dto.Gender))) profile.Gender = dto.Gender!.Trim();
            if (dto.Has(nameof(dto.Bio))) profile.Bio = dto.Bio!.Trim();
            if (dto.Has(nameof(dto.Intention))) profile.Intention = dto.Intention!.Trim();
            if (dto.Has(nameof(dto.Religion))) profile.Religion = dto.Religion!.Trim();
            if (dto.Has(nameof(dto.Workout))) profile.Workout = dto.Workout!.Trim();
            if (dto.Has(nameof(dto.Smoking))) profile.Smoking = dto.Smoking!.Trim();
            if (dto.Has(nameof(dto.Pets))) profile.Pets = dto.Pets!.Trim();
            if (dto.Has(nameof(dto.HeightCm))) profile.HeightCm = dto.HeightCm;
            if (dto.Has(nameof(dto.RelationshipHistory))) profile.RelationshipHistory = dto.RelationshipHistory!.Trim();
            if (dto.Has(nameof(dto.ZodiacSign))) profile.ZodiacSign = dto.ZodiacSign!.Trim();
            if (dto.Has(nameof(dto.Alcohol))) profile.Alcohol = dto.Alcohol!.Trim();
            if (dto.Has(nameof(dto.Cannabis))) profile.Cannabis = dto.Cannabis!.Trim();
            if (dto.Has(nameof(dto.ChildrenCount))) profile.ChildrenCount = dto.ChildrenCount!.Trim();
            if (dto.Has(nameof(dto.WantChildren))) profile.WantChildren = dto.WantChildren!.Trim();
            if (dto.Has(nameof(dto.WorkStatus))) profile.WorkStatus = dto.WorkStatus!.Trim();
            if (dto.Has(nameof(dto.StudyPlace))) profile.StudyPlace = dto.StudyPlace!.Trim();
            if (dto.Has(nameof(dto.StudySubject))) profile.StudySubject = dto.StudySubject!.Trim();
            if (dto.Has(nameof(dto.WorkPlace))) profile.WorkPlace = dto.WorkPlace!.Trim();
            if (dto.Has(nameof(dto.JobTitle))) profile.JobTitle = dto.JobTitle!.Trim();
            if (dto.Has(nameof(dto.LivePlace))) profile.LivePlace = dto.LivePlace!.Trim();
            if (dto.Has(nameof(dto.OriginPlace))) profile.OriginPlace = dto.OriginPlace!.Trim();
            if (dto.Has(nameof(dto.Interests))) profile.Interests = dto.Interests!.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (dto.Has(nameof(dto.PhotoUrls))) profile.PhotoUrls = dto.PhotoUrls!.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (dto.Has(nameof(dto.Lat))) profile.Lat = dto.Lat!.Value;
            if (dto.Has(nameof(dto.Lng))) profile.Lng = dto.Lng!.Value;
            if (dto.Has(nameof(dto.CountryCode))) profile.CountryCode = dto.CountryCode!.Trim();
            if (dto.Has(nameof(dto.InterestedIn))) profile.InterestedIn = dto.InterestedIn!.Trim();
            if (dto.Has(nameof(dto.PhotoUrls))) profile.IsVisible = profile.PhotoUrls.Count >= 2;
        }


        [HttpPost("profile")]
        [HttpPatch("profile")]
        public async Task<IActionResult> UpsertProfile([FromBody] UpsertProfileDto dto)
        {
            Guid meId;
            try { meId = CurrentUserService.GetUserIdOrThrow(User); }
            catch
            {
                return Unauthorized(new ErrorMessageResponseDto
                {
                    Message = "Invalid token."
                });
            }



            // Säkerställ att User finns (nu: riktiga userId från JWT)
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == meId);
            if (user == null)
            {
                return NotFound(new ErrorMessageResponseDto
                {
                    Message = "User not found. Register first."
                });
            }

            // Hämta eller skapa profil
            var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == meId);
            if (profile == null)
            {
                if (string.IsNullOrWhiteSpace(dto.DisplayName)) ModelState.AddModelError(nameof(dto.DisplayName), "DisplayName is required for a new profile.");
                if (dto.Age == null) ModelState.AddModelError(nameof(dto.Age), "Age is required for a new profile.");
                if (string.IsNullOrWhiteSpace(dto.Gender)) ModelState.AddModelError(nameof(dto.Gender), "Gender is required for a new profile.");
                if (!ModelState.IsValid) return ValidationProblem(ModelState);
                profile = new Profile { UserId = meId };
                _db.Profiles.Add(profile);
            }

            // Mappar DTO till Profile så logiken ligger på ett ställe
            MapUpsertProfileDtoToProfile(dto, profile);
            if (dto.Has(nameof(dto.MatchRadiusKm))) user.MatchRadiusKm = dto.MatchRadiusKm!.Value;

            await _db.SaveChangesAsync();

            //frontend kräver minst 2 bilder för att användare ska kunna gå vidare 
            return Ok(new UpsertProfileResponseDto
            {
                Message = "Profile saved",
                IsVisible = profile.IsVisible
            });
        }

        // ===============================
        // GET /me/profile
        // Hämta min profil
        // ===============================
        [HttpGet("profile")]
        public async Task<IActionResult> GetProfile()
        {
            Guid meId;
            try { meId = CurrentUserService.GetUserIdOrThrow(User); }
            catch
            {
                return Unauthorized(new ErrorMessageResponseDto
                {
                    Message = "Invalid token."
                });
            }

            var profile = await _db.Profiles
    .AsNoTracking()
    .Where(p => p.UserId == meId)
    .Select(p => new MyProfileDto
    {
        Id = p.Id,
        UserId = p.UserId,
        DisplayName = p.DisplayName,
        Age = p.Age,
        Gender = p.Gender,
        InterestedIn = p.InterestedIn,
        MatchRadiusKm = p.User!.MatchRadiusKm,
        Bio = p.Bio,
        Intention = p.Intention,
        Religion = p.Religion,
        Workout = p.Workout,
        Smoking = p.Smoking,
        Pets = p.Pets,
        HeightCm = p.HeightCm,
        RelationshipHistory = p.RelationshipHistory,
        ZodiacSign = p.ZodiacSign,
        Alcohol = p.Alcohol,
        Cannabis = p.Cannabis,
        ChildrenCount = p.ChildrenCount,
        WantChildren = p.WantChildren,
        WorkStatus = p.WorkStatus,
        StudyPlace = p.StudyPlace,
        StudySubject = p.StudySubject,
        WorkPlace = p.WorkPlace,
        JobTitle = p.JobTitle,
        LivePlace = p.LivePlace,
        OriginPlace = p.OriginPlace,
        Interests = p.Interests,
        PhotoUrls = p.PhotoUrls,
        Lat = p.Lat,
        Lng = p.Lng,
        CountryCode = p.CountryCode,
        IsVisible = p.IsVisible
    })
    .FirstOrDefaultAsync();

            if (profile == null)
            {
                return NotFound(new ErrorMessageResponseDto
                {
                    Message = "No profile yet."
                });
            }

            return Ok(profile);
        }

        
        // POST /me/radius
        // Uppdatera match-radius på min user

        [HttpPost("radius")]
        public async Task<IActionResult> UpdateRadius([FromBody] UpdateRadiusDto dto)
        {
            Guid meId;
            try { meId = CurrentUserService.GetUserIdOrThrow(User); }
            catch
            {
                return Unauthorized(new ErrorMessageResponseDto
                {
                    Message = "Invalid token."
                });
            }



            var user = await _db.Users.FindAsync(meId);
            if (user == null)
            {
                return NotFound(new ErrorMessageResponseDto
                {
                    Message = "User not found."
                });
            }

            user.MatchRadiusKm = dto.MatchRadiusKm;
            await _db.SaveChangesAsync();

            return Ok(new UpdateRadiusResponseDto
            {
                Message = "Radius updated",
                MatchRadiusKm = user.MatchRadiusKm
            });
        }

        // ===============================
        // POST /me/device-token
        // Spara Firebase device token
        // ===============================
        [HttpPost("device-token")]
        public async Task<IActionResult> SaveDeviceToken([FromBody] SaveDeviceTokenDto dto)
        {
            Guid meId;
            try { meId = CurrentUserService.GetUserIdOrThrow(User); }
            catch
            {
                return Unauthorized(new ErrorMessageResponseDto
                {
                    Message = "Invalid token."
                });
            }

            var trimmedToken = dto.Token.Trim();

            if (string.IsNullOrWhiteSpace(trimmedToken))
            {
                return BadRequest(new ErrorMessageResponseDto
                {
                    Message = "Device token is required."
                });
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == meId);
            if (user == null)
            {
                return NotFound(new ErrorMessageResponseDto
                {
                    Message = "User not found."
                });
            }

            // Viktigt:
            // samma device token får bara tillhöra ett konto åt gången.
            // Därför rensar jag först token från alla andra users.
            var otherUsersWithSameToken = await _db.Users
                .Where(u => u.Id != meId && u.DeviceToken == trimmedToken)
                .ToListAsync();

            foreach (var otherUser in otherUsersWithSameToken)
            {
                otherUser.DeviceToken = null;
            }

            user.DeviceToken = trimmedToken;

            await _db.SaveChangesAsync();

            return Ok(new
            {
                message = "Device token saved"
            });
        }

        [HttpPost("device-token/clear")]
        public async Task<IActionResult> ClearDeviceToken()
        {
            Guid meId;
            try { meId = CurrentUserService.GetUserIdOrThrow(User); }
            catch
            {
                return Unauthorized(new ErrorMessageResponseDto
                {
                    Message = "Invalid token."
                });
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == meId);
            if (user == null)
            {
                return NotFound(new ErrorMessageResponseDto
                {
                    Message = "User not found."
                });
            }

            // Logout / account switch:
            // koppla loss device token från nuvarande konto.
            user.DeviceToken = null;

            await _db.SaveChangesAsync();

            return Ok(new
            {
                message = "Device token cleared"
            });
        }

    }
}