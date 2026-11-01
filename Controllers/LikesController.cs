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
    private readonly UserRelationService _userRelationService;
    private readonly CommerceService? _commerce;

    public LikesController(AppDbContext db, UserRelationService userRelationService, CommerceService? commerce = null)
    {
        _db = db;
        _userRelationService = userRelationService;
        _commerce = commerce;
    }



    private IQueryable<Zullo.Api.Models.Like> EligibleLikes(Guid me) => _db.Likes.AsNoTracking()
        .Where(l => l.ToUserId == me && l.FromUserId != me &&
            _db.Profiles.Any(p => p.UserId == l.FromUserId && p.IsVisible && p.User != null && !p.User.IsPaused && !p.User.IsClosed) &&
            !_db.Matches.Any(m => (m.UserAId == me && m.UserBId == l.FromUserId) || (m.UserBId == me && m.UserAId == l.FromUserId)) &&
            !_db.Blocks.Any(b => (b.FromUserId == me && b.BlockedUserId == l.FromUserId) || (b.FromUserId == l.FromUserId && b.BlockedUserId == me)));

    private async Task<bool> Premium(Guid me)
    {
        if (_commerce != null) await _commerce.RefreshBenefitsIfSubscribed(me);
        return CommerceService.Premium(await _db.CommerceAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == me));
    }

    // Counts contain no sender IDs, names or image URLs, including for locked likes.
    [HttpGet("received/summary")]
    public async Task<IActionResult> Summary()
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        var premium = await Premium(me);
        var likes = EligibleLikes(me);
        var counts = await likes.GroupBy(_ => 1).Select(g => new {
            Total = g.Count(), Hidden = g.Count(l => !l.IsSuperLike)
        }).SingleOrDefaultAsync();
        return Ok(new { totalCount = counts?.Total ?? 0, hiddenCount = premium ? 0 : counts?.Hidden ?? 0, canViewAll = premium });
    }

    [HttpGet("received")]
    public async Task<IActionResult> GetReceivedLikes()
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        var premium = await Premium(me);
        var likes = await EligibleLikes(me).Where(l => premium || l.IsSuperLike).ToListAsync();
        var ids = likes.Select(l => l.FromUserId).ToList();
        var profiles = await _db.Profiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToListAsync();
        return Ok(profiles.Select(p => new ReceivedLikeDto {
            UserId = p.UserId, IsSuperLike = likes.Any(l => l.FromUserId == p.UserId && l.IsSuperLike),
            DisplayName = p.DisplayName, Age = p.Age, PhotoUrl = p.PhotoUrls?.FirstOrDefault() ?? ""
        }).ToList());
    }
}
