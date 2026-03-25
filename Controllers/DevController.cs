using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Zullo.Api.Dtos;

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

        // ✅ Ta en befintlig profil som "center" (slipp hårdkodad userId)
        var meProfile = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync();
        if (meProfile == null)
            return BadRequest("Skapa minst 1 profil först (POST /me/profile).");

        var now = DateTime.UtcNow;

        for (int i = 0; i < count; i++)
        {
            var userId = Guid.NewGuid();

            var user = new User
            {
                Id = userId,
                IsVerified = true,
                Email = $"test{i}@zullo.local",

                // ✅ obligatoriska fält (viktigt!)
                CreatedAtUtc = now,
                LikesRemaining = 999,
                LikesResetAtUtc = now.AddHours(12),
                MatchRadiusKm = 50
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

                // ✅ Seed nära "meProfile"
                Lat = meProfile.Lat + (Random.Shared.NextDouble() - 0.5) * 0.4,
                Lng = meProfile.Lng + (Random.Shared.NextDouble() - 0.5) * 0.4,
                CountryCode = meProfile.CountryCode,

                IsVisible = true
            };

            _db.User.Add(user);      // Om din DbSet heter User (som i din migration) så är detta rätt
            _db.Profiles.Add(profile);
        }

        await _db.SaveChangesAsync();
        return Ok(new SeedResponseDto
        {
            Message = "Seed done",
            Created = count
        });
    }

    // POST /dev/clear-seed
    [HttpPost("clear-seed")]
    public async Task<IActionResult> ClearSeed()
    {
        var users = await _db.User
            .Where(u => u.Email != null && u.Email.EndsWith("@zullo.local"))
            .ToListAsync();

        var userIds = users.Select(u => u.Id).ToList();

        if (userIds.Count == 0)
        {
            return Ok(new ClearSeedResponseDto
            {
                Message = "Seed cleared",
                RemovedUsers = 0
            });
        }

        var profiles = await _db.Profiles
            .Where(p => userIds.Contains(p.UserId))
            .ToListAsync();

        var likes = await _db.Likes
            .Where(x => userIds.Contains(x.FromUserId) || userIds.Contains(x.ToUserId))
            .ToListAsync();

        var skips = await _db.Skips
            .Where(x => userIds.Contains(x.FromUserId) || userIds.Contains(x.ToUserId))
            .ToListAsync();

        var matches = await _db.Matches
            .Where(x => userIds.Contains(x.UserAId) || userIds.Contains(x.UserBId))
            .ToListAsync();

        var messages = await _db.Messages
            .Where(x => userIds.Contains(x.FromUserId) || userIds.Contains(x.ToUserId))
            .ToListAsync();

        var blocks = await _db.Blocks
            .Where(x => userIds.Contains(x.FromUserId) || userIds.Contains(x.BlockedUserId))
            .ToListAsync();

        var reports = await _db.Reports
            .Where(x => userIds.Contains(x.FromUserId) || userIds.Contains(x.ReportedUserId))
            .ToListAsync();

        // Ta bort beroende rader först så FK-relationer inte blockerar
        _db.Likes.RemoveRange(likes);
        _db.Skips.RemoveRange(skips);
        _db.Matches.RemoveRange(matches);
        _db.Messages.RemoveRange(messages);
        _db.Blocks.RemoveRange(blocks);
        _db.Reports.RemoveRange(reports);
        _db.Profiles.RemoveRange(profiles);
        _db.User.RemoveRange(users);

        await _db.SaveChangesAsync();

        return Ok(new ClearSeedResponseDto
        {
            Message = "Seed cleared",
            RemovedUsers = users.Count
        });
    }

    // GET /dev/stats
    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var profilesTotal = await _db.Profiles.CountAsync();
        var profilesVisible = await _db.Profiles.CountAsync(p => p.IsVisible);
        var likes = await _db.Likes.CountAsync();
        var skips = await _db.Skips.CountAsync();

        return Ok(new DevStatsDto
        {
            ProfilesTotal = profilesTotal,
            ProfilesVisible = profilesVisible,
            Likes = likes,
            Skips = skips
        });
    }
    [HttpGet("whoami")]
    [Authorize]
    public IActionResult WhoAmI()
    {
        var nameId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = User.FindFirstValue(ClaimTypes.Email);

        return Ok(new WhoAmIResponseDto
        {
            NameIdentifier = nameId,
            Email = email,
            AllClaims = User.Claims
         .Select(c => new ClaimItemDto
         {
             Type = c.Type,
             Value = c.Value
         })
         .ToList()
        });
    }
}


