using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("matches")]
public class MatchesController : ControllerBase
{
    private readonly AppDbContext _db;

    // TEMP user id tills riktig auth
    private static readonly Guid TempUserId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    public MatchesController(AppDbContext db)
    {
        _db = db;
    }

    // GET /matches
    [HttpGet]
    public async Task<IActionResult> GetMyMatches()
    {
        var myId = TempUserId;

        var matches = await _db.Matches.AsNoTracking()
            .Where(m => m.UserAId == myId || m.UserBId == myId)
            .OrderByDescending(m => m.CreatedAtUtc)
            .ToListAsync();

        // hämta "andra" userId för varje match
        var otherIds = matches
            .Select(m => m.UserAId == myId ? m.UserBId : m.UserAId)
            .Distinct()
            .ToList();

        // hämta profiler för de andra
        var profiles = await _db.Profiles.AsNoTracking()
            .Where(p => otherIds.Contains(p.UserId))
            .Select(p => new
            {
                userId = p.UserId,
                displayName = p.DisplayName,
                age = p.Age,
                countryCode = p.CountryCode,
                photoUrl = p.PhotoUrls.Count > 0 ? p.PhotoUrls[0] : ""
            })
            .ToListAsync();

        // mappa till match-lista (med createdAt)
        var result = matches.Select(m =>
        {
            var otherId = (m.UserAId == myId) ? m.UserBId : m.UserAId;
            var p = profiles.FirstOrDefault(x => x.userId == otherId);

            return new
            {
                matchId = m.Id,
                createdAtUtc = m.CreatedAtUtc,
                otherUserId = otherId,
                displayName = p?.displayName ?? "Unknown",
                age = p?.age ?? 0,
                countryCode = p?.countryCode ?? "",
                photoUrl = p?.photoUrl ?? ""
            };
        });

        return Ok(new { matches = result });
    }
}
