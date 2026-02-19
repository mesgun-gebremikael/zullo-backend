using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("matches")]
public class MatchesController : ControllerBase
{
    private readonly AppDbContext _db;

    private static readonly Guid TempUserId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    public MatchesController(AppDbContext db)
    {
        _db = db;
    }

    // ✅ GET /matches
    [HttpGet] // ⭐ DEN HÄR MÅSTE FINNAS
    public async Task<IActionResult> GetMyMatches()
    {
        var myMatches = await _db.Matches.AsNoTracking()
            .Where(m => m.UserAId == TempUserId || m.UserBId == TempUserId)
            .ToListAsync();

        var otherIds = myMatches
            .Select(m => m.UserAId == TempUserId ? m.UserBId : m.UserAId)
            .ToList();

        var profilesRaw = await _db.Profiles.AsNoTracking()
            .Where(p => otherIds.Contains(p.UserId))
            .ToListAsync();

        var profiles = profilesRaw.Select(p => new
        {
            userId = p.UserId,
            displayName = p.DisplayName,
            age = p.Age,
            photoUrl = (p.PhotoUrls != null && p.PhotoUrls.Count > 0) ? p.PhotoUrls[0] : ""
        }).ToList();

        return Ok(profiles);
    }

    [HttpPost("force-match")]
    public async Task<IActionResult> ForceMatch([FromQuery] Guid targetUserId)
    {
        var meId = TempUserId;

        var likeExists = await _db.Likes.AnyAsync(l =>
            l.FromUserId == targetUserId && l.ToUserId == meId);

        if (!likeExists)
        {
            _db.Likes.Add(new Like { FromUserId = targetUserId, ToUserId = meId });
        }

        var matchExists = await _db.Matches.AnyAsync(m =>
            (m.UserAId == meId && m.UserBId == targetUserId) ||
            (m.UserAId == targetUserId && m.UserBId == meId));

        if (!matchExists)
        {
            _db.Matches.Add(new Match { UserAId = meId, UserBId = targetUserId });
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Force match created", targetUserId });
    }
}
