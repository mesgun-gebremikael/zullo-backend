using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Services;
namespace Zullo.Api.Controllers;

[ApiController, Authorize, Route("profiles")]
public class MatchedProfileController(AppDbContext db, UserRelationService relations) : ControllerBase
{
    [HttpGet("{other:guid}")]
    public async Task<IActionResult> Get(Guid other)
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        if ((await relations.GetBlockedUserIdsAsync(me)).Contains(other)) return NotFound();
        if (!await db.Matches.AnyAsync(m => (m.UserAId == me && m.UserBId == other) || (m.UserBId == me && m.UserAId == other))) return NotFound();
        var viewer = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == me);
        var profile = await db.Profiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == other && !p.User!.IsClosed);
        return viewer == null || profile == null ? NotFound() : Ok(ProfilePresentation.ToDto(profile, viewer));
    }
}
