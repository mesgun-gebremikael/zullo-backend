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
    private readonly UserRelationService _userRelationService;

    public SwipeController(AppDbContext db, LikeLimitService likeLimit, UserRelationService userRelationService)
    {
        _db = db;
        _likeLimit = likeLimit;
        _userRelationService = userRelationService;
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
        {
            return Unauthorized(new ErrorMessageResponseDto
            {
                Message = "Invalid token."
            });
        }

        // Hämta min user (för radie)
        var me = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == meId);
        if (me == null)
        {
            return Unauthorized(new ErrorMessageResponseDto
            {
                Message = "User not found."
            });
        }

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
        {
            return BadRequest(new ErrorMessageResponseDto
            {
                Message = "Create your profile first (POST /me/profile)."
            });
        }

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

        var blockedIds = await _userRelationService.GetBlockedUserIdsAsync(meId);
        var excluded = likedIds
          .Concat(skippedIds)
           .Concat(blockedIds)
          .ToHashSet();
        excluded.Add(meId);

        // Hämta kandidater (SQL slutar här)
        var candidates = await _db.Profiles.AsNoTracking()
     // Endast profiler som är markerade synliga
     .Where(p => p.IsVisible)

     // Extra säkerhet: feeden ska bara visa profiler med minst 2 bilder
     .Where(p => p.PhotoUrls != null && p.PhotoUrls.Count >= 2)

     .Where(p => !excluded.Contains(p.UserId))
     .Where(p => p.Age >= minAge && p.Age <= maxAge)
     .Take(200)
     .ToListAsync();



        // Räkna avstånd och filtrera inom radien (C#)
        var result = candidates
     .Select(p => new SwipeProfileDto
     {
         UserId = p.UserId,

         // skyddar mot null och konstiga värden
         DisplayName = (p.DisplayName ?? "").Trim(),
         Age = p.Age,
         Bio = (p.Bio ?? "").Trim(),

         Intention = p.Intention,
         Religion = p.Religion,
         Workout = p.Workout,
         Smoking = p.Smoking,
         Pets = p.Pets,

         // säkerställ att listor aldrig är null
         Interests = p.Interests ?? new List<string>(),
         PhotoUrls = p.PhotoUrls ?? new List<string>(),

         // första bild om den finns
         PhotoUrl = p.PhotoUrls?.FirstOrDefault() ?? "",

         CountryCode = (p.CountryCode ?? "").Trim(),

         DistanceKm = Math.Round(
             GeoService.DistanceKm(myLat, myLng, p.Lat, p.Lng),
             1
         )
     })
        // visa bara profiler som har minst 1 bild
       .Where(x => x.PhotoUrls.Count > 0)
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
        {
            return Unauthorized(new ErrorMessageResponseDto
            {
                Message = "Invalid token."
            });
        }

        // Anti-spam: max 1 like per 500 ms
        var halfSecondAgo = DateTime.UtcNow.AddMilliseconds(-500);

        var recentLike = await _db.Likes
            .Where(l => l.FromUserId == meId)
            .OrderByDescending(l => l.CreatedAtUtc)
            .FirstOrDefaultAsync();

        if (recentLike != null && recentLike.CreatedAtUtc > halfSecondAgo)
        {
            return BadRequest(new ErrorMessageResponseDto
            {
                Message = "You're swiping too fast."
            });
        }

        // 1) kolla om like redan finns
        var already = await _db.Likes.AnyAsync(l =>
            l.FromUserId == meId && l.ToUserId == dto.TargetUserId);

        if (!already)
        {
            // 2) bara om det är en ny like -> konsumera like
            var ok = await _likeLimit.TryConsumeLikeAsync(meId);
            if (!ok)
                return StatusCode(429, new ErrorMessageResponseDto
                {
                    Message = "Like limit reached. Try again later."
                });

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

            var matchExists = await _userRelationService.IsMatchedAsync(userAId, userBId);
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
        {
            return Unauthorized(new ErrorMessageResponseDto
            {
                Message = "Invalid token."
            });
        }

        // Anti-spam: max 1 skip per 500 ms
        var halfSecondAgo = DateTime.UtcNow.AddMilliseconds(-500);

        var recentSkip = await _db.Skips
            .Where(s => s.FromUserId == meId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync();

        if (recentSkip != null && recentSkip.CreatedAtUtc > halfSecondAgo)
        {
            return BadRequest(new ErrorMessageResponseDto
            {
                Message = "You're swiping too fast."
            });
        }

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

        return Ok(new SkipResponseDto
        {
            Skipped = true
        });
    }
}