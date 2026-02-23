using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("matches")]
[Authorize]
public class MatchesController : ControllerBase
{
    private readonly AppDbContext _db;

    public MatchesController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetMeId()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(idStr) || !Guid.TryParse(idStr, out var meId))
            throw new Exception("Missing/invalid NameIdentifier claim.");
        return meId;
    }

    // ✅ GET /matches
    [HttpGet]
    public async Task<IActionResult> GetMyMatches()
    {
        var meId = GetMeId();

        var myMatches = await _db.Matches.AsNoTracking()
            .Where(m => m.UserAId == meId || m.UserBId == meId)
            .ToListAsync();

        var otherIds = myMatches
            .Select(m => m.UserAId == meId ? m.UserBId : m.UserAId)
            .Distinct()
            .ToList();

        if (otherIds.Count == 0)
            return Ok(new List<object>());

        var profilesRaw = await _db.Profiles.AsNoTracking()
            .Where(p => otherIds.Contains(p.UserId))
            .ToListAsync();

        var lastMsgs = await _db.Messages.AsNoTracking()
            .Where(m =>
                (m.FromUserId == meId && otherIds.Contains(m.ToUserId)) ||
                (m.ToUserId == meId && otherIds.Contains(m.FromUserId))
            )
            .GroupBy(m => m.FromUserId == meId ? m.ToUserId : m.FromUserId)
            .Select(g => new
            {
                otherUserId = g.Key,

                lastMessageText = g.OrderByDescending(x => x.CreatedAtUtc)
                    .Select(x => x.Text).FirstOrDefault(),

                lastMessageAtUtc = g.OrderByDescending(x => x.CreatedAtUtc)
                    .Select(x => (DateTime?)x.CreatedAtUtc).FirstOrDefault(),

                hasUnread = g.Any(x =>
                    x.FromUserId == g.Key &&
                    x.ToUserId == meId &&
                    x.ReadAtUtc == null
                ),
            })
            .ToListAsync();

        var lastMap = lastMsgs.ToDictionary(x => x.otherUserId, x => x);

        var result = profilesRaw.Select(p =>
        {
            lastMap.TryGetValue(p.UserId, out var last);

            return new
            {
                userId = p.UserId,
                displayName = p.DisplayName,
                age = p.Age,
                photoUrl = (p.PhotoUrls != null && p.PhotoUrls.Count > 0) ? p.PhotoUrls[0] : "",
                lastMessageText = last?.lastMessageText,
                lastMessageAtUtc = last?.lastMessageAtUtc,
                hasUnread = last?.hasUnread ?? false
            };
        }).ToList();

        // sort: senaste först
        result = result
            .OrderByDescending(x => x.lastMessageAtUtc ?? DateTime.MinValue)
            .ToList();

        return Ok(result);
    }

    // ✅ POST /matches/force-match?targetUserId=GUID
    [HttpPost("force-match")]
    public async Task<IActionResult> ForceMatch([FromQuery] Guid targetUserId)
    {
        var meId = GetMeId();

        if (targetUserId == Guid.Empty) return BadRequest("targetUserId is required.");
        if (targetUserId == meId) return BadRequest("Cannot match yourself.");

        var matchExists = await _db.Matches.AnyAsync(m =>
            (m.UserAId == meId && m.UserBId == targetUserId) ||
            (m.UserAId == targetUserId && m.UserBId == meId));

        if (!matchExists)
        {
            _db.Matches.Add(new Match { UserAId = meId, UserBId = targetUserId });
            await _db.SaveChangesAsync();
        }

        return Ok(new { message = "Force match created", meId, targetUserId });
    }
}