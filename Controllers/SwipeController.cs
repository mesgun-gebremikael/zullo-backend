using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("swipe")]
public class SwipeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LikeLimitService _likeLimit;

    // TEMP user id tills riktig auth
    private static readonly Guid TempUserId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    public SwipeController(AppDbContext db, LikeLimitService likeLimit)
    {
        _db = db;
        _likeLimit = likeLimit;
    }

    // GET /swipe/feed
    // Returnerar några profiler som användaren inte redan swipat
    [HttpGet("feed")]
    public async Task<IActionResult> GetFeed([FromQuery] int take = 10)
    {
        take = Math.Clamp(take, 1, 50);

        // Hämta min user (för radie)
        var me = await _db.User.AsNoTracking().FirstAsync(u => u.Id == TempUserId);
        var myRadiusKm = me.MatchRadiusKm;

        // Hämta min profil (för lat/lng)
        var myProfile = await _db.Profiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == TempUserId);

        if (myProfile == null)
            return BadRequest("Create your profile first (POST /me/profile).");

        var myLat = myProfile.Lat;
        var myLng = myProfile.Lng;

        // Exkludera redan swipade
        var likedIds = await _db.Likes
            .Where(l => l.FromUserId == TempUserId)
            .Select(l => l.ToUserId)
            .ToListAsync();

        var skippedIds = await _db.Skips
            .Where(s => s.FromUserId == TempUserId)
            .Select(s => s.ToUserId)
            .ToListAsync();

        var excluded = likedIds.Concat(skippedIds).ToHashSet();
        excluded.Add(TempUserId);

        // Hämta kandidater (vi tar fler än "take" och filtrerar på avstånd efteråt)
        var candidates = await _db.Profiles.AsNoTracking()
            .Where(p => p.IsVisible)
            .Where(p => !excluded.Contains(p.UserId))
            .Take(200) // tar lite fler, sen filtrerar vi
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
        // 1) kolla like-limit
        var ok = await _likeLimit.TryConsumeLikeAsync(TempUserId);
        if (!ok)
        {
            return StatusCode(429, new { message = "Like limit reached. Try again later." });
        }

        // 2) spara like
        var already = await _db.Likes.AnyAsync(l =>
            l.FromUserId == TempUserId && l.ToUserId == dto.TargetUserId);

        if (!already)
        {
            _db.Likes.Add(new Like
            {
                FromUserId = TempUserId,
                ToUserId = dto.TargetUserId
            });
            await _db.SaveChangesAsync();
        }

        // 3) match om den andra redan har gillat mig
        var reciprocal = await _db.Likes.AnyAsync(l =>
            l.FromUserId == dto.TargetUserId && l.ToUserId == TempUserId);

        if (reciprocal)
        {
            var matchExists = await _db.Matches.AnyAsync(m =>
                (m.UserAId == TempUserId && m.UserBId == dto.TargetUserId) ||
                (m.UserAId == dto.TargetUserId && m.UserBId == TempUserId));

            if (!matchExists)
            {
                _db.Matches.Add(new Match
                {
                    UserAId = TempUserId,
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
        var already = await _db.Skips.AnyAsync(s =>
            s.FromUserId == TempUserId && s.ToUserId == dto.TargetUserId);

        if (!already)
        {
            _db.Skips.Add(new Skip
            {
                FromUserId = TempUserId,
                ToUserId = dto.TargetUserId
            });
            await _db.SaveChangesAsync();
        }

        return Ok(new { skipped = true });
    }
}
