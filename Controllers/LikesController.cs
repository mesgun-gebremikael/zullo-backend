using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zullo.Api.Data;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("likes")]
[Authorize]
public class LikesController : ControllerBase
{
    private readonly AppDbContext _db;

    public LikesController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetMeIdOrThrow()
    {
        var meIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(meIdStr, out var meId))
            throw new Exception("Invalid token user id.");
        return meId;
    }

    // GET /likes/received
    [HttpGet("received")]
    public async Task<IActionResult> GetLikesReceived()
    {
        Guid meId;
        try { meId = GetMeIdOrThrow(); }
        catch { return Unauthorized(); }

        // 1) vilka har gillat mig?
        var likerIds = await _db.Likes.AsNoTracking()
            .Where(l => l.ToUserId == meId)
            .Select(l => l.FromUserId)
            .Distinct()
            .ToListAsync();

        if (likerIds.Count == 0)
            return Ok(new List<object>());

        // 2) hämta profiler
        var profiles = await _db.Profiles.AsNoTracking()
            .Where(p => likerIds.Contains(p.UserId))
            .ToListAsync();

        var result = profiles.Select(p => new
        {
            userId = p.UserId,
            displayName = p.DisplayName,
            age = p.Age,
            photoUrl = (p.PhotoUrls != null && p.PhotoUrls.Count > 0) ? p.PhotoUrls[0] : ""
        }).ToList();

        return Ok(result);
    }
}