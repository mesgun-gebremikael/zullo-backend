using System.Security.Claims;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("matches")]
[Authorize] // kräver JWT
public class MatchesController : ControllerBase
{
    private readonly AppDbContext _db;

    public MatchesController(AppDbContext db)
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

    // GET /matches
    [HttpGet]
    public async Task<IActionResult> GetMyMatches()
    {
        Guid meId;
        try { meId = GetMeIdOrThrow(); }
        catch { return Unauthorized(); }

        // 1) Hämta mina matches
        var myMatches = await _db.Matches.AsNoTracking()
            .Where(m => m.UserAId == meId || m.UserBId == meId)
            .ToListAsync();

        // Hämta blockerade användare
        var blockedIds = await _db.Blocks
            .Where(b => b.FromUserId == meId || b.BlockedUserId == meId)
            .Select(b => b.FromUserId == meId ? b.BlockedUserId : b.FromUserId)
            .ToListAsync();

        //ta bort blockerade matcher
         myMatches = myMatches
         .Where(m =>
         {
             var otherId = m.UserAId == meId ? m.UserBId : m.UserAId;
              return !blockedIds.Contains(otherId);
         })
            .ToList();

        // map: otherUserId -> matchCreatedAtUtc (för sort fallback när ingen message finns)
        var matchCreatedMap = myMatches.ToDictionary(
            m => (m.UserAId == meId ? m.UserBId : m.UserAId),
            m => m.CreatedAtUtc
        );

        // 2) Plocka ut "andra personens userId" för varje match
        var otherIds = myMatches
            .Select(m => m.UserAId == meId ? m.UserBId : m.UserAId)
            .Distinct()
            .ToList();

        if (otherIds.Count == 0)
            return Ok(new List<object>());

        // 3) Hämta profiler för de andra användarna (SQL slutar här)
        var profilesRaw = await _db.Profiles.AsNoTracking()
            .Where(p => otherIds.Contains(p.UserId))
            .ToListAsync();

        // 4) Hämta senaste meddelande per otherUserId + hasUnread
        var lastMsgs = await _db.Messages.AsNoTracking()
            .Where(m =>
                (m.FromUserId == meId && otherIds.Contains(m.ToUserId)) ||
                (m.ToUserId == meId && otherIds.Contains(m.FromUserId))
            )
            .GroupBy(m => m.FromUserId == meId ? m.ToUserId : m.FromUserId)
            .Select(g => new
            {
                otherUserId = g.Key,

                lastMessageText = g
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Select(x => x.Text)
                    .FirstOrDefault(),

                lastMessageAtUtc = g
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Select(x => (DateTime?)x.CreatedAtUtc)
                    .FirstOrDefault(),

                // Olästa = meddelanden från other -> mig som saknar ReadAtUtc
                hasUnread = g.Any(x =>
                    x.FromUserId == g.Key &&
                    x.ToUserId == meId &&
                    x.ReadAtUtc == null
                ),
            })
            .ToListAsync();

        var lastMap = lastMsgs.ToDictionary(x => x.otherUserId, x => x);

        // 5) Slå ihop profiler + last message i ett svar till frontend (C#)
        var result = profilesRaw.Select(p =>
        {
            lastMap.TryGetValue(p.UserId, out var last);

            return new
            {
                userId = p.UserId,
                displayName = p.DisplayName,
                age = p.Age,

                //  jsonb-safe (ingen Count, ingen [0])
                photoUrl = p.PhotoUrls?.FirstOrDefault() ?? "",

                lastMessageText = last?.lastMessageText,
                lastMessageAtUtc = last?.lastMessageAtUtc,

                matchCreatedAtUtc = matchCreatedMap.TryGetValue(p.UserId, out var mc)
                    ? mc
                    : (DateTime?)null,

                hasUnread = last?.hasUnread ?? false
            };
        })
        // Sortera: unread först, sen senaste message, annars matchCreatedAtUtc
        .OrderByDescending(x => x.hasUnread)
        .ThenByDescending(x => x.lastMessageAtUtc ?? x.matchCreatedAtUtc ?? DateTime.MinValue)
        .ToList();

        return Ok(result);
    }

    // POST /matches/force-match?targetUserId=<GUID>
    // För test: skapar Like + Match om det saknas
    [HttpPost("force-match")]
    public async Task<IActionResult> ForceMatch([FromQuery] Guid targetUserId)
    {
        Guid meId;
        try { meId = GetMeIdOrThrow(); }
        catch { return Unauthorized(); }

        if (targetUserId == Guid.Empty) return BadRequest("targetUserId is required.");
        if (targetUserId == meId) return BadRequest("Cannot match with yourself.");

        // Skapa Like (target -> me) om den inte finns (bara för test)
        var likeExists = await _db.Likes.AnyAsync(l =>
            l.FromUserId == targetUserId && l.ToUserId == meId);

        if (!likeExists)
        {
            _db.Likes.Add(new Like { FromUserId = targetUserId, ToUserId = meId });
        }

        // Skapa Match om den inte finns (A/B spelar ingen roll)
        var matchExists = await _db.Matches.AnyAsync(m =>
            (m.UserAId == meId && m.UserBId == targetUserId) ||
            (m.UserAId == targetUserId && m.UserBId == meId));

        if (!matchExists)
        {
            _db.Matches.Add(new Match { UserAId = meId, UserBId = targetUserId });
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            message = "Force match created",
            meId,
            targetUserId
        });
    }
}