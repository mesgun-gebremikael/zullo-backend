using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Models;
using Zullo.Api.Services;
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
            // Trimma textfält så vi inte sparar onödiga mellanslag i databasen
            var trimmedDisplayName = dto.DisplayName.Trim();
            var trimmedGender = dto.Gender.Trim();
            var trimmedBio = dto.Bio.Trim();
            var trimmedCountryCode = dto.CountryCode.Trim();

            // Grundfält för profil
            profile.DisplayName = trimmedDisplayName;
            profile.Age = dto.Age;
            profile.Gender = trimmedGender;
            profile.Bio = trimmedBio;

            // Dating-preferenser / livsstil
            profile.Intention = dto.Intention.Trim();
            profile.Religion = dto.Religion.Trim();
            profile.Workout = dto.Workout.Trim();
            profile.Smoking = dto.Smoking.Trim();
            profile.Pets = dto.Pets.Trim();
            // New expanded profile fields
            profile.HeightCm = dto.HeightCm;

            profile.RelationshipHistory = dto.RelationshipHistory.Trim();
            profile.ZodiacSign = dto.ZodiacSign.Trim();

            profile.Alcohol = dto.Alcohol.Trim();
            profile.Cannabis = dto.Cannabis.Trim();

            profile.ChildrenCount = dto.ChildrenCount.Trim();
            profile.WantChildren = dto.WantChildren.Trim();

            profile.WorkStatus = dto.WorkStatus.Trim();
            profile.StudyPlace = dto.StudyPlace.Trim();
            profile.StudySubject = dto.StudySubject.Trim();
            profile.WorkPlace = dto.WorkPlace.Trim();
            profile.JobTitle = dto.JobTitle.Trim();

            profile.LivePlace = dto.LivePlace.Trim();
            profile.OriginPlace = dto.OriginPlace.Trim();

            // Trimma listor och ta bort tomma värden
            profile.Interests = (dto.Interests ?? new())
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            profile.PhotoUrls = (dto.PhotoUrls ?? new())
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            // Position / land
            profile.Lat = dto.Lat;
            profile.Lng = dto.Lng;
            profile.CountryCode = trimmedCountryCode;

            // Synlig först när minst 2 bilder finns
            profile.IsVisible = profile.PhotoUrls.Count >= 2;
        }


        [HttpPost("profile")]
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
                profile = new Profile { UserId = meId };
                _db.Profiles.Add(profile);
            }

            // Mappar DTO till Profile så logiken ligger på ett ställe
            MapUpsertProfileDtoToProfile(dto, profile);

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
            // Därför rensar vi först token från alla andra users.
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

    }
}