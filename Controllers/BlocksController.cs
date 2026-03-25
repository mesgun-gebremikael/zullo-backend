using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Dtos;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("blocks")]
[Authorize]
public class BlocksController : ControllerBase
{
    private readonly AppDbContext _db;

    public BlocksController(AppDbContext db)
    {
        _db = db;
    }

  

    

    // POST /blocks
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBlockDto dto)
    {
        Guid meId;

        try
        {
            meId = CurrentUserService.GetUserIdOrThrow(User);
        }
        catch
        {
            return Unauthorized();
        }

        if (dto.BlockedUserId == Guid.Empty)
            return BadRequest("blockedUserId required");

        if (dto.BlockedUserId == meId)
            return BadRequest("Cannot block yourself");

        var alreadyBlocked = await _db.Blocks
            .AnyAsync(x => x.FromUserId == meId && x.BlockedUserId == dto.BlockedUserId);

        if (alreadyBlocked)
            return Ok(new { message = "Already blocked" });

        var block = new Block
        {
            FromUserId = meId,
            BlockedUserId = dto.BlockedUserId
        };

        _db.Blocks.Add(block);
        await _db.SaveChangesAsync();

        return Ok(new { message = "User blocked" });
    }
}
