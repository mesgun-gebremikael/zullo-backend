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
            // Grundfält för profil
            profile.DisplayName = dto.DisplayName;
            profile.Age = dto.Age;
            profile.Gender = dto.Gender;
            profile.Bio = dto.Bio;

            // Dating-preferenser / livsstil
            profile.Intention = dto.Intention;
            profile.Religion = dto.Religion;
            profile.Workout = dto.Workout;
            profile.Smoking = dto.Smoking;
            profile.Pets = dto.Pets;

            // Listor från frontend
            profile.Interests = dto.Interests ?? new();
            profile.PhotoUrls = dto.PhotoUrls ?? new();

            // Position / land
            profile.Lat = dto.Lat;
            profile.Lng = dto.Lng;
            profile.CountryCode = dto.CountryCode;

            // Synlig först när minst 2 bilder finns
            profile.IsVisible = profile.PhotoUrls.Count >= 2;
        }


        [HttpPost("profile")]
        public async Task<IActionResult> UpsertProfile([FromBody] UpsertProfileDto dto)
        {
            Guid meId;
            try { meId = CurrentUserService.GetUserIdOrThrow(User); }
            catch { return Unauthorized(); }

            if (dto.Age < 18)
                return BadRequest("Age must be 18+.");

            // Säkerställ att User finns (nu: riktiga userId från JWT)
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == meId);
            if (user == null)
                return NotFound("User not found. Register first.");

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
            catch { return Unauthorized(); }

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
        Interests = p.Interests,
        PhotoUrls = p.PhotoUrls,
        Lat = p.Lat,
        Lng = p.Lng,
        CountryCode = p.CountryCode,
        IsVisible = p.IsVisible
    })
    .FirstOrDefaultAsync();

            if (profile == null)
                return NotFound("No profile yet.");

            return Ok(profile);
        }

        // ===============================
        // POST /me/radius
        // Uppdatera match-radius på min user
        // ===============================
        [HttpPost("radius")]
        public async Task<IActionResult> UpdateRadius([FromBody] UpdateRadiusDto dto)
        {
            Guid meId;
            try { meId = CurrentUserService.GetUserIdOrThrow(User); }
            catch { return Unauthorized(); }

            if (dto.MatchRadiusKm < 1 || dto.MatchRadiusKm > 200)
                return BadRequest("MatchRadiusKm must be between 1 and 200.");

            var user = await _db.Users.FindAsync(meId);
            if (user == null) return NotFound("User not found.");

            user.MatchRadiusKm = dto.MatchRadiusKm;
            await _db.SaveChangesAsync();

            return Ok(new UpdateRadiusResponseDto
            {
                Message = "Radius updated",
                MatchRadiusKm = user.MatchRadiusKm
            });
        }
    }
}