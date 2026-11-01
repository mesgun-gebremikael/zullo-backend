using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Dtos;
using Zullo.Api.Services;
using Zullo.Api.Hubs;

namespace Zullo.Api.Controllers;

public record BlockedProfileDto(Guid UserId, string DisplayName);

[ApiController, Route("blocks"), Authorize]
public class BlocksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<ChatHub> _hub;
    private readonly ILogger<BlocksController> _logger;
    public BlocksController(AppDbContext db, IHubContext<ChatHub> hub, ILogger<BlocksController> logger) { _db = db; _hub = hub; _logger = logger; }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        // Only outgoing blocks: never disclose who blocked the caller.
        var rows = await (from block in _db.Blocks.AsNoTracking()
                          where block.FromUserId == me
                          join profile in _db.Profiles on block.BlockedUserId equals profile.UserId into profiles
                          from profile in profiles.DefaultIfEmpty()
                          orderby block.CreatedAtUtc descending
                          select new BlockedProfileDto(block.BlockedUserId,
                              profile == null ? "Profil" : profile.DisplayName)).ToListAsync();
        return Ok(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBlockDto dto)
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        var other = dto.BlockedUserId;
        if (other == Guid.Empty || other == me) return BadRequest();
        if (!await _db.Users.AnyAsync(u => u.Id == other)) return NotFound();
        await using var transaction = await RelationTransaction.Begin(_db, me, other);
        if (!await _db.Users.AnyAsync(u => u.Id == other && !u.IsClosed)) return NotFound();
        if (!await _db.Blocks.AnyAsync(b => b.FromUserId == me && b.BlockedUserId == other))
            _db.Blocks.Add(new Block { Id = Guid.NewGuid(), FromUserId = me, BlockedUserId = other });
        await ResetSwipes(me, other);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        await Notify(me, other);
        return Ok(new SimpleMessageResponseDto { Message = "User blocked" });
    }

    [HttpDelete("{other:guid}")]
    public async Task<IActionResult> Delete(Guid other)
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        await using var transaction = await RelationTransaction.Begin(_db, me, other);
        var block = await _db.Blocks.SingleOrDefaultAsync(b => b.FromUserId == me && b.BlockedUserId == other);
        if (block == null) return NoContent();
        _db.Blocks.Remove(block);
        await ResetSwipes(me, other);
        // A fresh match starts a fresh conversation, never resurrects old messages.
        _db.Messages.RemoveRange(await _db.Messages.Where(m =>
            (m.FromUserId == me && m.ToUserId == other) || (m.FromUserId == other && m.ToUserId == me)).ToListAsync());
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        await Notify(me, other);
        return NoContent();
    }

    private async Task ResetSwipes(Guid me, Guid other)
    {
        _db.Matches.RemoveRange(await _db.Matches.Where(m =>
            (m.UserAId == me && m.UserBId == other) || (m.UserAId == other && m.UserBId == me)).ToListAsync());
        _db.Likes.RemoveRange(await _db.Likes.Where(l =>
            (l.FromUserId == me && l.ToUserId == other) || (l.FromUserId == other && l.ToUserId == me)).ToListAsync());
        _db.Skips.RemoveRange(await _db.Skips.Where(s =>
            (s.FromUserId == me && s.ToUserId == other) || (s.FromUserId == other && s.ToUserId == me)).ToListAsync());
    }

    private async Task Notify(Guid me, Guid other)
    {
        // Durable state is already committed. A disconnected client reloads on resume.
        try
        {
            await _hub.Clients.User(me.ToString()).SendAsync("RelationChanged", new { otherUserId = other });
            await _hub.Clients.User(other.ToString()).SendAsync("RelationChanged", new { otherUserId = me });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Relationship notification failed; clients reconcile on reconnect."); }
    }
}
