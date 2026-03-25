using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Services;

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

   

    // GET /likes/received
    [HttpGet("received")]
    public async Task<IActionResult> GetReceivedLikes()
    {
        Guid meId;
        try { meId = CurrentUserService.GetUserIdOrThrow(User); }
        catch { return Unauthorized(); }

        // 1️ Hämta userIds som har gillat mig
        var likedMeIds = await _db.Likes.AsNoTracking()
            .Where(l => l.ToUserId == meId)
            .Select(l => l.FromUserId)
            .Distinct()
            .ToListAsync();

        if (likedMeIds.Count == 0)
            return Ok(new List<ReceivedLikeDto>());

        // 2️ Hämta redan matchade userIds (så vi kan filtrera bort dem)
        var matchedIds = await _db.Matches.AsNoTracking()
            .Where(m => m.UserAId == meId || m.UserBId == meId)
            .Select(m => m.UserAId == meId ? m.UserBId : m.UserAId)
            .ToListAsync();

        // 3️ Filtrera bort de som redan är matchade
        var filteredIds = likedMeIds
            .Where(id => !matchedIds.Contains(id))
            .ToList();

        if (filteredIds.Count == 0)
            return Ok(new List<ReceivedLikeDto>());

        // 4️⃣ Hämta profiler (först till minne)
        var profilesRaw = await _db.Profiles.AsNoTracking()
            .Where(p => filteredIds.Contains(p.UserId))
            .ToListAsync();

        // 5️⃣ Bygg svaret i C# (inte i SQL) -> då funkar jsonb alltid
        var result = profilesRaw.Select(p => new ReceivedLikeDto
        {
            UserId = p.UserId,
            DisplayName = p.DisplayName,
            Age = p.Age,

            // jsonb-safe: ta första bilden om den finns
            PhotoUrl = p.PhotoUrls?.FirstOrDefault() ?? ""
        }).ToList();

        return Ok(result);
    }
}