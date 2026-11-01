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
    private readonly CommerceService? _commerce;
    private readonly Microsoft.AspNetCore.SignalR.IHubContext<Zullo.Api.Hubs.ChatHub>? _hub;

    public SwipeController(AppDbContext db, LikeLimitService likeLimit, UserRelationService userRelationService, CommerceService? commerce = null, Microsoft.AspNetCore.SignalR.IHubContext<Zullo.Api.Hubs.ChatHub>? hub = null)
    {
        _db = db;
        _likeLimit = likeLimit;
        _userRelationService = userRelationService;
        _commerce = commerce;
        _hub = hub;
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
        if (me.IsPaused || me.IsClosed) return Ok(new SwipeFeedResponseDto { RadiusKm = myRadiusKm, Profiles = new(), IsPaused = me.IsPaused });

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

        var preferredGender = myProfile.InterestedIn switch
        {
            "Män" => "Man",
            "Kvinnor" => "Kvinna",
            _ => null // Existing profiles keep their previous unfiltered behavior.
        };

        var now = DateTime.UtcNow;
        var superLikedMe = await _db.Likes.AsNoTracking().Where(l => l.ToUserId == meId && l.IsSuperLike).Select(l => l.FromUserId).ToListAsync();
        var boosted = await _db.CommerceAccounts.AsNoTracking().Where(a => a.BoostUntilUtc > now).Select(a => a.UserId).ToListAsync();
        // Hämta kandidater (SQL slutar här)
        var candidates = await _db.Profiles.AsNoTracking()
            // Endast profiler som är markerade synliga
            .Where(p => !p.User!.IsPaused && !p.User.IsClosed && p.IsVisible)
            .Where(p => !_db.CommerceAccounts.Any(a => a.UserId == p.UserId && a.Incognito && a.PremiumUntilUtc > now) ||
                _db.Likes.Any(l => l.FromUserId == p.UserId && l.ToUserId == meId))
            .Where(p => preferredGender == null || p.Gender == preferredGender)

            // ❌ FEL (borttagen):
            // .Where(p => p.PhotoUrls != null && p.PhotoUrls.Count >= 2)
            // Den här kraschar PostgreSQL (jsonb + cardinality)

            .Where(p => !excluded.Contains(p.UserId))
            .Where(p => p.Age >= minAge && p.Age <= maxAge)
            .OrderByDescending(p => superLikedMe.Contains(p.UserId))
            .ThenByDescending(p => boosted.Contains(p.UserId))
            .Take(200)
            .ToListAsync();


        // Räkna avstånd och filtrera inom radien (C#)
        var result = candidates
            .Select(p => new SwipeProfileDto
            {
                UserId = p.UserId,
                SuperLikedYou = superLikedMe.Contains(p.UserId),

                // skyddar mot null och konstiga värden
                DisplayName = (p.DisplayName ?? "").Trim(),
                Age = p.Age,
                Bio = (p.Bio ?? "").Trim(),

                Intention = (p.Intention ?? "").Trim(),
                Religion = (p.Religion ?? "").Trim(),
                Workout = (p.Workout ?? "").Trim(),
                Smoking = (p.Smoking ?? "").Trim(),
                Pets = (p.Pets ?? "").Trim(),

                HeightCm = p.HeightCm,

                RelationshipHistory = (p.RelationshipHistory ?? "").Trim(),
                ZodiacSign = (p.ZodiacSign ?? "").Trim(),

                Alcohol = (p.Alcohol ?? "").Trim(),
                Cannabis = (p.Cannabis ?? "").Trim(),

                ChildrenCount = (p.ChildrenCount ?? "").Trim(),
                WantChildren = (p.WantChildren ?? "").Trim(),

                WorkStatus = (p.WorkStatus ?? "").Trim(),
                StudyPlace = (p.StudyPlace ?? "").Trim(),
                StudySubject = (p.StudySubject ?? "").Trim(),
                WorkPlace = (p.WorkPlace ?? "").Trim(),
                JobTitle = (p.JobTitle ?? "").Trim(),

                LivePlace = (p.LivePlace ?? "").Trim(),
                OriginPlace = (p.OriginPlace ?? "").Trim(),

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

            // ✅ FIX (flyttad hit istället för SQL):
            // visa bara profiler som har minst 2 bilder
            .Where(x => x.PhotoUrls != null && x.PhotoUrls.Count >= 2)

            // ❌ gammal kommentar:
            // .Where(x => x.PhotoUrls.Count > 0)

            .Where(x => x.DistanceKm <= myRadiusKm)
            .OrderByDescending(x => x.SuperLikedYou)
            .ThenByDescending(x => boosted.Contains(x.UserId))
            .ThenBy(x => x.DistanceKm)
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

        if (await _db.Users.AnyAsync(u => u.Id == meId && u.IsPaused))
            return Conflict(new { message = "Återaktivera kontot innan du swipar." });
        if (dto.TargetUserId == meId || dto.TargetUserId == Guid.Empty) return BadRequest();
        if (!await _db.Users.AnyAsync(u => u.Id == dto.TargetUserId && !u.IsPaused && !u.IsClosed)) return NotFound();
        if (_commerce != null) await _commerce.RefreshBenefitsIfSubscribed(meId);
        await using var relationTransaction = await RelationTransaction.Begin(_db, meId, dto.TargetUserId);
        if (await _db.Users.AnyAsync(u => u.Id == meId && u.IsPaused))
            return Conflict(new { message = "Återaktivera kontot innan du swipar." });
        if (!await _db.Users.AnyAsync(u => u.Id == dto.TargetUserId && !u.IsPaused && !u.IsClosed)) return NotFound();

        if (_commerce != null) await _commerce.Lock(meId);

        // A direct API request must respect blocks just like the swipe feed.
        if (await _userRelationService.IsBlockedAsync(meId, dto.TargetUserId))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorMessageResponseDto
            {
                Message = "You cannot like this user."
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

        if (already && dto.SuperLike && !await _db.Likes.AnyAsync(l => l.FromUserId == meId && l.ToUserId == dto.TargetUserId && l.IsSuperLike))
            return Conflict(new { message = "Du har redan gillat profilen." });
        if (!already)
        {
            if (dto.SuperLike)
            {
                if (!await _db.Profiles.AnyAsync(p => p.UserId == dto.TargetUserId && p.IsVisible)) return NotFound();
                if (_commerce == null) return StatusCode(503);
                await _commerce.Lock(meId);
                var account = await _commerce.Account(meId);
                if (!CommerceService.ConsumeSuperLike(account)) return StatusCode(402, new { message = "Du har inga Superlikes kvar." });
            }
            // 2) bara om det är en ny like -> konsumera like
            var ok = dto.SuperLike || await _likeLimit.TryConsumeLikeAsync(meId);
            if (!ok)
                return StatusCode(429, new ErrorMessageResponseDto
                {
                    Message = "Like limit reached. Try again later."
                });

            // 3) spara like
            _db.Likes.Add(new Like
            {
                FromUserId = meId,
                ToUserId = dto.TargetUserId,
                IsSuperLike = dto.SuperLike
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

            await relationTransaction.CommitAsync();
            if (!already) await NotifyIncomingLike(dto.TargetUserId);
            if (dto.SuperLike && !already) await NotifySuperLike(meId, dto.TargetUserId);
            return Ok(new LikeResponseDto
            {
                Matched = true
            });
        }

        await relationTransaction.CommitAsync();
        if (!already) await NotifyIncomingLike(dto.TargetUserId);
        if (dto.SuperLike && !already) await NotifySuperLike(meId, dto.TargetUserId);
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

        await using var relationTransaction = await RelationTransaction.Begin(_db, meId, dto.TargetUserId);
        if (await _db.Users.AnyAsync(u => u.Id == meId && u.IsPaused))
            return Conflict(new { message = "Återaktivera kontot innan du swipar." });
        if (!await _db.Users.AnyAsync(u => u.Id == dto.TargetUserId && !u.IsPaused && !u.IsClosed)) return NotFound();
        if (_commerce != null) await _commerce.Lock(meId);
        if (await _userRelationService.IsBlockedAsync(meId, dto.TargetUserId)) return StatusCode(403);

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

        await relationTransaction.CommitAsync();
        return Ok(new SkipResponseDto
        {
            Skipped = true
        });
    }
    private async Task NotifyIncomingLike(Guid recipient)
    {
        if (_hub == null) return;
        try {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            // Invalidation only: no identity or profile details bypass the Premium gate.
            await Microsoft.AspNetCore.SignalR.ClientProxyExtensions.SendAsync(
                _hub.Clients.User(recipient.ToString()), "LikesChanged", new { }, deadline.Token);
        } catch { /* Notification delivery must not undo the committed like. */ }
    }

    private async Task NotifySuperLike(Guid from, Guid to)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (_hub != null) await Microsoft.AspNetCore.SignalR.ClientProxyExtensions.SendAsync(
                _hub.Clients.User(to.ToString()), "SuperLikeReceived", new { senderUserId = from }, deadline.Token);
            var token = await _db.Users.AsNoTracking().Where(u => u.Id == to && !u.IsClosed).Select(u => u.DeviceToken).SingleOrDefaultAsync();
            if (!string.IsNullOrWhiteSpace(token))
                await FirebaseAdmin.Messaging.FirebaseMessaging.DefaultInstance.SendAsync(new FirebaseAdmin.Messaging.Message
                {
                    Token = token,
                    Notification = new FirebaseAdmin.Messaging.Notification { Title = "ZULLO ⭐", Body = "Någon har supergillat dig. Öppna Swipa för att se profilen." },
                    Android = new FirebaseAdmin.Messaging.AndroidConfig { Priority = FirebaseAdmin.Messaging.Priority.High, Notification = new FirebaseAdmin.Messaging.AndroidNotification { ChannelId = "messages", Sound = "default" } },
                    Data = new Dictionary<string, string> { ["type"] = "super_like", ["recipientUserId"] = to.ToString() }
                }, deadline.Token);
        }
        catch (Exception ex) { HttpContext.RequestServices.GetService<ILogger<SwipeController>>()?.LogWarning(ex, "Superlike notification could not be delivered."); }
    }

    [HttpPost("rewind")]
    public async Task<IActionResult> Rewind()
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        if (_commerce == null) return StatusCode(503);
        await _commerce.State(me);
        var lastLike = await _db.Likes.AsNoTracking().Where(l => l.FromUserId == me).OrderByDescending(l => l.CreatedAtUtc).FirstOrDefaultAsync();
        var lastSkip = await _db.Skips.AsNoTracking().Where(l => l.FromUserId == me).OrderByDescending(l => l.CreatedAtUtc).FirstOrDefaultAsync();
        var undoLike = lastLike != null && (lastSkip == null || lastLike.CreatedAtUtc >= lastSkip.CreatedAtUtc);
        var target = undoLike ? lastLike!.ToUserId : lastSkip?.ToUserId;
        if (target == null) return NotFound(new { message = "Det finns ingen swipe att ångra." });
        await using var transaction = await RelationTransaction.Begin(_db, me, target.Value);
        await _commerce.Lock(me);
        var newestLike = await _db.Likes.AsNoTracking().Where(l => l.FromUserId == me).OrderByDescending(l => l.CreatedAtUtc).Select(l => (Guid?)l.Id).FirstOrDefaultAsync();
        var newestSkip = await _db.Skips.AsNoTracking().Where(l => l.FromUserId == me).OrderByDescending(l => l.CreatedAtUtc).Select(l => (Guid?)l.Id).FirstOrDefaultAsync();
        if (newestLike != lastLike?.Id || newestSkip != lastSkip?.Id) return Conflict(new { message = "Din senaste swipe har ändrats. Försök igen." });
        var account = await _commerce.Account(me);
        if (!CommerceService.Premium(account)) return StatusCode(402);
        if (await _userRelationService.IsBlockedAsync(me, target.Value) || await _userRelationService.IsMatchedAsync(me, target.Value) || (undoLike && lastLike!.IsSuperLike))
            return Conflict(new { message = "En match eller Superlike kan inte ångras här." });
        if (undoLike) _db.Likes.Remove(lastLike!); else _db.Skips.Remove(lastSkip!);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return Ok(new { userId = target });
    }

}