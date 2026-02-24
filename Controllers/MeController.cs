using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Models;

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

        private Guid GetMeIdOrThrow()
        {
            var meIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(meIdStr) || !Guid.TryParse(meIdStr, out var meId))
                throw new UnauthorizedAccessException("Missing/invalid user id in token.");
            return meId;
        }

        // ===============================
        // POST /me/profile
        // Skapa eller uppdatera min profil
        // ===============================
        [HttpPost("profile")]
        public async Task<IActionResult> UpsertProfile([FromBody] UpsertProfileDto dto)
        {
            Guid meId;
            try { meId = GetMeIdOrThrow(); }
            catch { return Unauthorized(); }

            if (dto.Age < 18)
                return BadRequest("Age must be 18+.");

            // Säkerställ att User finns (nu: riktiga userId från JWT)
            var user = await _db.User.FirstOrDefaultAsync(u => u.Id == meId);
            if (user == null)
                return NotFound("User not found. Register first.");

            // Hämta eller skapa profil
            var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == meId);
            if (profile == null)
            {
                profile = new Profile { UserId = meId };
                _db.Profiles.Add(profile);
            }

            // Uppdatera profilfält
            profile.DisplayName = dto.DisplayName;
            profile.Age = dto.Age;
            profile.Gender = dto.Gender;
            profile.Bio = dto.Bio;

            profile.Intention = dto.Intention;
            profile.Religion = dto.Religion;

            profile.Workout = dto.Workout;
            profile.Smoking = dto.Smoking;
            profile.Pets = dto.Pets;

            profile.Interests = dto.Interests ?? new();
            profile.PhotoUrls = dto.PhotoUrls ?? new();

            profile.Lat = dto.Lat;
            profile.Lng = dto.Lng;
            profile.CountryCode = dto.CountryCode;

            // Synlig endast om minst 2 bilder (behåller din logik)
            profile.IsVisible = profile.PhotoUrls.Count >= 2;

            await _db.SaveChangesAsync();

            return Ok(new
            {
                message = "Profile saved",
                isVisible = profile.IsVisible
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
            try { meId = GetMeIdOrThrow(); }
            catch { return Unauthorized(); }

            var profile = await _db.Profiles
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == meId);

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
            try { meId = GetMeIdOrThrow(); }
            catch { return Unauthorized(); }

            if (dto.MatchRadiusKm < 1 || dto.MatchRadiusKm > 200)
                return BadRequest("MatchRadiusKm must be between 1 and 200.");

            var user = await _db.User.FindAsync(meId);
            if (user == null) return NotFound("User not found.");

            user.MatchRadiusKm = dto.MatchRadiusKm;
            await _db.SaveChangesAsync();

            return Ok(new { message = "Radius updated", user.MatchRadiusKm });
        }
    }
}