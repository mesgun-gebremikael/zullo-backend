using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Linq;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Services;
using Zullo.Api.Dtos;


namespace Zullo.Api.Controllers;

[ApiController]
[Route("swipe")]
[Authorize] // kräver JWT på alla endpoints i denna controller
public class SwipeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly LikeLimitService _likeLimit;

    public SwipeController(AppDbContext db, LikeLimitService likeLimit)
    {
        _db = db;
        _likeLimit = likeLimit;
    }

    private async Task<List<Guid>> GetBlockedUserIdsAsync(Guid meId)
    {
        // Hämtar alla användare som jag blockat eller som blockat mig
        return await _db.Blocks.AsNoTracking()
            .Where(b => b.FromUserId == meId || b.BlockedUserId == meId)
            .Select(b => b.FromUserId == meId ? b.BlockedUserId : b.FromUserId)
            .ToListAsync();
    }

    private async Task<bool> MatchExistsAsync(Guid userAId, Guid userBId)
    {
        // Match sparas nu i fast ordning: lägsta Guid först
        var firstUserId = userAId.CompareTo(userBId) < 0 ? userAId : userBId;
        var secondUserId = userAId.CompareTo(userBId) < 0 ? userBId : userAId;

        return await _db.Matches.AnyAsync(m =>
            m.UserAId == firstUserId && m.UserBId == secondUserId);
    }


    // GET /swipe/feed
    [HttpGet("feed")]
    public async Task<IActionResult> GetFeed(
        [FromQuery] int minAge = 18,
        [FromQuery] int maxAge = 99,
        [FromQuery] int take = 10)
    {
        take = Math.Clamp(take, 1, 50);

        if (!CurrentUserService.TryGetUserId(User, out var meId))
            return Unauthorized("Invalid token.");

        // Hämta min user (för radie)
        var me = await _db.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == meId);
        if (me == null)
            return Unauthorized("User not found.");

        var myRadiusKm = me.MatchRadiusKm;

        if (minAge < 18) minAge = 18;
        if (maxAge > 100) maxAge = 100;

        if (minAge > maxAge)
        {
            return Ok(new SwipeFeedResponseDto
            {
                RadiusKm = myRadiusKm,
                Profiles = new List<SwipeProfileDto>()
            });
        }


        // var preferredGender = me.PreferredGender; // 

        // Hämta min profil (för lat/lng)
        var myProfile = await _db.Profiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == meId);

        if (myProfile == null)
            return BadRequest("Create your profile first (POST /me/profile).");

        var myLat = myProfile.Lat;
        var myLng = myProfile.Lng;

        // Exkludera redan swipade
        var likedIds = await _db.Likes.AsNoTracking()
            .Where(l => l.FromUserId == meId)
            .Select(l => l.ToUserId)
            .ToListAsync();

        var skippedIds = await _db.Skips.AsNoTracking()
            .Where(s => s.FromUserId == meId)
            .Select(s => s.ToUserId)
            .ToListAsync();

        var blockedIds = await GetBlockedUserIdsAsync(meId);

        var excluded = likedIds
          .Concat(skippedIds)
           .Concat(blockedIds)
          .ToHashSet();
        excluded.Add(meId);

        // Hämta kandidater (SQL slutar här)
        var candidates = await _db.Profiles.AsNoTracking()
          .Where(p => p.IsVisible)
           .Where(p => !excluded.Contains(p.UserId))

          // 🎯 FILTER
            .Where(p => p.Age >= minAge && p.Age <= maxAge)

              .Take(200)
         .ToListAsync();

        

        // Räkna avstånd och filtrera inom radien (C#)
        var result = candidates
     .Select(p => new SwipeProfileDto
     {
         UserId = p.UserId,
         DisplayName = p.DisplayName,
         Age = p.Age,
         Bio = p.Bio,
         Intention = p.Intention,
         Religion = p.Religion,
         Workout = p.Workout,
         Smoking = p.Smoking,
         Pets = p.Pets,
         Interests = p.Interests,
         PhotoUrls = p.PhotoUrls,

         // jsonb-safe: första bild utan Count eller index
         PhotoUrl = p.PhotoUrls?.FirstOrDefault() ?? "",

         CountryCode = p.CountryCode,
         DistanceKm = Math.Round(GeoService.DistanceKm(myLat, myLng, p.Lat, p.Lng), 1)
     })
     .Where(x => x.DistanceKm <= myRadiusKm)
     .OrderBy(x => x.DistanceKm)
     .Take(take)
     .ToList();

        return Ok(new SwipeFeedResponseDto
        {
            RadiusKm = myRadiusKm,
            Profiles = result
        });
    }


    // POST /swipe/like
    [HttpPost("like")]
    public async Task<IActionResult> Like([FromBody] SwipeTargetDto dto)
    {
        if (!CurrentUserService.TryGetUserId(User, out var meId))
            return Unauthorized("Invalid token.");

        // 1) kolla om like redan finns
        var already = await _db.Likes.AnyAsync(l =>
            l.FromUserId == meId && l.ToUserId == dto.TargetUserId);

        if (!already)
        {
            // 2) bara om det är en ny like -> konsumera like
            var ok = await _likeLimit.TryConsumeLikeAsync(meId);
            if (!ok)
                return StatusCode(429, new { message = "Like limit reached. Try again later." });

            // 3) spara like
            _db.Likes.Add(new Like
            {
                FromUserId = meId,
                ToUserId = dto.TargetUserId
            });
            await _db.SaveChangesAsync();
        }

        var reciprocal = await _db.Likes.AnyAsync(l =>
     l.FromUserId == dto.TargetUserId && l.ToUserId == meId);

        if (reciprocal)
        {
            // Spara alltid match i samma ordning för att undvika A/B och B/A-dubbletter
            var userAId = meId.CompareTo(dto.TargetUserId) < 0 ? meId : dto.TargetUserId;
            var userBId = meId.CompareTo(dto.TargetUserId) < 0 ? dto.TargetUserId : meId;

            var matchExists = await MatchExistsAsync(userAId, userBId);

            if (!matchExists)
            {
                _db.Matches.Add(new Match
                {
                    UserAId = userAId,
                    UserBId = userBId
                });

                await _db.SaveChangesAsync();
            }

            return Ok(new LikeResponseDto
            {
                Matched = true
            });
        }

        return Ok(new LikeResponseDto
        {
            Matched = false
        });
    }

    // POST /swipe/skip
    [HttpPost("skip")]
    public async Task<IActionResult> Skip([FromBody] SwipeTargetDto dto)
    {
        if (!CurrentUserService.TryGetUserId(User, out var meId))
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