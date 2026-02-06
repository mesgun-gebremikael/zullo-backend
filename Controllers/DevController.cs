using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("dev")]
public class DevController : ControllerBase
{
    private readonly AppDbContext _db;

    public DevController(AppDbContext db)
    {
        _db = db;
    }

    // POST /dev/seed?count=20
    [HttpPost("seed")]
    public async Task<IActionResult> Seed([FromQuery] int count = 20)
    {
        count = Math.Clamp(count, 1, 200);

        // Hämta min profil så vi seed:ar nära mig (inte Stockholm-hårdkodat)
        var meProfile = await _db.Profiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == Guid.Parse("11111111-1111-1111-1111-111111111111"));

        if (meProfile == null)
            return BadRequest("Create your profile first (POST /me/profile).");

        for (int i = 0; i < count; i++)
        {
            var userId = Guid.NewGuid();

            var user = new User
            {
                Id = userId,
                IsVerified = true,
                Email = $"test{i}@zullo.local"
            };

            var profile = new Profile
            {
                UserId = userId,
                DisplayName = $"Test {i + 1}",
                Age = 18 + (i % 20),
                Gender = (i % 2 == 0) ? "Man" : "Kvinna",
                Bio = "Testprofil för swipe-flödet",

                Intention = (IntentionType)(i % 3),
                Religion = ReligionType.Private,

                Workout = (TriState)(i % 3),
                Smoking = (TriState)((i + 1) % 3),
                Pets = (PetsType)(i % 3),

                Interests = new List<string> { "Gym", "Resor", "Musik" },

                PhotoUrls = new List<string>
                {
                    $"https://picsum.photos/seed/{userId}/600/800",
                    $"https://picsum.photos/seed/{userId}-2/600/800"
                },

                // Seed nära MIG (automatisk)
                Lat = meProfile.Lat + (Random.Shared.NextDouble() - 0.5) * 0.4,
                Lng = meProfile.Lng + (Random.Shared.NextDouble() - 0.5) * 0.4,
                CountryCode = meProfile.CountryCode,

                IsVisible = true
            };

            _db.User.Add(user);       // ✅ Users (inte User)
            _db.Profiles.Add(profile);
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Seed done", created = count });
    }

    // POST /dev/clear-seed
    [HttpPost("clear-seed")]
    public async Task<IActionResult> ClearSeed()
    {
        var users = await _db.User   // ✅ Users (inte User)
            .Where(u => u.Email != null && u.Email.EndsWith("@zullo.local"))
            .ToListAsync();

        var userIds = users.Select(u => u.Id).ToList();

        var profiles = await _db.Profiles
            .Where(p => userIds.Contains(p.UserId))
            .ToListAsync();

        _db.Profiles.RemoveRange(profiles);
        _db.User.RemoveRange(users); // ✅ Users (inte User)

        await _db.SaveChangesAsync();
        return Ok(new { message = "Seed cleared", removedUsers = users.Count });
    }

    // GET /dev/stats
    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var profilesTotal = await _db.Profiles.CountAsync();
        var profilesVisible = await _db.Profiles.CountAsync(p => p.IsVisible);
        var likes = await _db.Likes.CountAsync();
        var skips = await _db.Skips.CountAsync();

        return Ok(new
        {
            profilesTotal,
            profilesVisible,
            likes,
            skips
        });
    }
    // GET /dev/check-distances
    [HttpGet("check-distances")]
    public async Task<IActionResult> CheckDistances()
    {
        var meId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var me = await _db.Profiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == meId);

        if (me == null)
            return BadRequest("Create your profile first (POST /me/profile).");

        // ta 10 synliga profiler (inte jag själv)
        var others = await _db.Profiles.AsNoTracking()
            .Where(p => p.IsVisible)
            .Where(p => p.UserId != meId)
            .Take(10)
            .Select(p => new
            {
                p.UserId,
                p.DisplayName,
                p.Lat,
                p.Lng,
                p.CountryCode
            })
            .ToListAsync();

        // räkna avstånd i C# (samma som feed gör)
        var result = others.Select(p => new
        {
            p.UserId,
            p.DisplayName,
            p.Lat,
            p.Lng,
            p.CountryCode,
            distanceKm = Math.Round(Zullo.Api.Services.GeoService.DistanceKm(me.Lat, me.Lng, p.Lat, p.Lng), 1)
        });

        return Ok(new { me = new { me.Lat, me.Lng, me.CountryCode }, samples = result });
    }
}
