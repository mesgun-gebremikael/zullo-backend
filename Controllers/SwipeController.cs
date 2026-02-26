using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("swipe")]
[Authorize] // ✅ kräver JWT på alla endpoints i denna controller
public class SwipeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LikeLimitService _likeLimit;

    public SwipeController(AppDbContext db, LikeLimitService likeLimit)
    {
        _db = db;
        _likeLimit = likeLimit;
    }

    // Liten hjälpfunktion: plockar ut userId från JWT
    private bool TryGetMeId(out Guid meId)
    {
        meId = Guid.Empty;
        var meIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(meIdStr, out meId);
    }

    // GET /swipe/feed
    [HttpGet("feed")]
    public async Task<IActionResult> GetFeed([FromQuery] int take = 10)
    {
        take = Math.Clamp(take, 1, 50);

        if (!TryGetMeId(out var meId))
            return Unauthorized("Invalid token.");

        // Hämta min user (för radie)
        var me = await _db.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == meId);
        if (me == null)
            return Unauthorized("User not found.");

        var myRadiusKm = me.MatchRadiusKm;

        // Hämta min profil (för lat/lng)
        var myProfile = await _db.Profiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == meId);

        if (myProfile == null)
            return BadRequest("Create your profile first (POST /me/profile).");

        var myLat = myProfile.Lat;
        var myLng = myProfile.Lng;

        // Exkludera redan swipade
        var likedIds = await _db.Likes
            .Where(l => l.FromUserId == meId)
            .Select(l => l.ToUserId)
            .ToListAsync();

        var skippedIds = await _db.Skips
            .Where(s => s.FromUserId == meId)
            .Select(s => s.ToUserId)
            .ToListAsync();

        var excluded = likedIds.Concat(skippedIds).ToHashSet();
        excluded.Add(meId);

        // Hämta kandidater
        var candidates = await _db.Profiles.AsNoTracking()
            .Where(p => p.IsVisible)
            .Where(p => !excluded.Contains(p.UserId))
            .Take(200)
            .ToListAsync();

        // Räkna avstånd och filtrera inom radien
        var result = candidates
            .Select(p => new
            {
                p.UserId,
                p.DisplayName,
                p.Age,
                p.Bio,
                p.Intention,
                p.Religion,
                p.Workout,
                p.Smoking,
                p.Pets,
                p.Interests,
                p.PhotoUrls,
                // ✅ säker "första bild" för UI (om du vill)
                photoUrl = (p.PhotoUrls != null && p.PhotoUrls.Count > 0) ? p.PhotoUrls[0] : "",
                p.CountryCode,
                distanceKm = Math.Round(GeoService.DistanceKm(myLat, myLng, p.Lat, p.Lng), 1)
            })
            .Where(x => x.distanceKm <= myRadiusKm)
            .OrderBy(x => x.distanceKm)
            .Take(take)
            .ToList();

        return Ok(new { radiusKm = myRadiusKm, profiles = result });
    }

    public record SwipeTargetDto(Guid TargetUserId);

    // POST /swipe/like
    [HttpPost("like")]
    public async Task<IActionResult> Like([FromBody] SwipeTargetDto dto)
    {
        if (!TryGetMeId(out var meId))
            return Unauthorized("Invalid token.");

        // 1) kolla like-limit
        var ok = await _likeLimit.TryConsumeLikeAsync(meId);
        if (!ok)
            return StatusCode(429, new { message = "Like limit reached. Try again later." });

        // 2) spara like
        var already = await _db.Likes.AnyAsync(l =>
            l.FromUserId == meId && l.ToUserId == dto.TargetUserId);

        if (!already)
        {
            _db.Likes.Add(new Like
            {
                FromUserId = meId,
                ToUserId = dto.TargetUserId
            });
            await _db.SaveChangesAsync();
        }

        // 3) match om den andra redan har gillat mig
        var reciprocal = await _db.Likes.AnyAsync(l =>
            l.FromUserId == dto.TargetUserId && l.ToUserId == meId);

        if (reciprocal)
        {
            var matchExists = await _db.Matches.AnyAsync(m =>
                (m.UserAId == meId && m.UserBId == dto.TargetUserId) ||
                (m.UserAId == dto.TargetUserId && m.UserBId == meId));

            if (!matchExists)
            {
                _db.Matches.Add(new Match
                {
                    UserAId = meId,
                    UserBId = dto.TargetUserId
                });
                await _db.SaveChangesAsync();
            }

            return Ok(new { matched = true });
        }

        return Ok(new { matched = false });
    }

    // POST /swipe/skip
    [HttpPost("skip")]
    public async Task<IActionResult> Skip([FromBody] SwipeTargetDto dto)
    {
        if (!TryGetMeId(out var meId))
            return Unauthorized("Invalid token.");

        var already = await _db.Skips.AnyAsync(s =>
            s.FromUserId == meId && s.ToUserId == dto.TargetUserId);

        if (!already)
        {
            _db.Skips.Add(new Skip
            {
                FromUserId = meId,
                ToUserId = dto.TargetUserId
            });
            await _db.SaveChangesAsync();
        }

        return Ok(new { skipped = true });
    }
}