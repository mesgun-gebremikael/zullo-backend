using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Dtos;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("messages")]
[Authorize] //  kräver JWT
public class MessagesController : ControllerBase
{
    private readonly AppDbContext _db;

    public MessagesController(AppDbContext db)
    {
        _db = db;
    }


    private async Task<bool> IsBlockedAsync(Guid meId, Guid otherUserId)
    {
        return await _db.Blocks.AnyAsync(b =>
            (b.FromUserId == meId && b.BlockedUserId == otherUserId) ||
            (b.FromUserId == otherUserId && b.BlockedUserId == meId));
    }

    private async Task<bool> IsMatchedAsync(Guid meId, Guid otherUserId)
    {
        // Chat är bara tillåten mellan två användare som har matchat
        return await _db.Matches.AnyAsync(m =>
            (m.UserAId == meId && m.UserBId == otherUserId) ||
            (m.UserAId == otherUserId && m.UserBId == meId));
    }

    // GET /messages/thread?otherUserId=GUID
    [HttpGet("thread")]
    public async Task<IActionResult> GetThread([FromQuery] Guid otherUserId)
    {
        var meId = CurrentUserService.GetUserIdOrThrow(User);

        var isBlocked = await IsBlockedAsync(meId, otherUserId);
        if (isBlocked)
            return Forbid();

        var isMatched = await IsMatchedAsync(meId, otherUserId);
        if (!isMatched)
            return Forbid();

        var msgs = await _db.Messages.AsNoTracking()
            .Where(m =>
                (m.FromUserId == meId && m.ToUserId == otherUserId) ||
                (m.FromUserId == otherUserId && m.ToUserId == meId))
            .OrderBy(m => m.CreatedAtUtc)
           .Select(m => new MessageDto
           {
               Id = m.Id,
               FromUserId = m.FromUserId,
               ToUserId = m.ToUserId,
               Text = m.Text,
               CreatedAtUtc = m.CreatedAtUtc,
               ReadAtUtc = m.ReadAtUtc
           })
            .ToListAsync();

        return Ok(msgs);
    }


    // POST /messages/send
    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendMessageDto dto)
    {
        var meId = CurrentUserService.GetUserIdOrThrow(User);

        var isBlocked = await IsBlockedAsync(meId, dto.ToUserId);
        if (isBlocked)
            return Forbid();

        if (dto.ToUserId == Guid.Empty) return BadRequest("ToUserId is required.");
        if (string.IsNullOrWhiteSpace(dto.Text)) return BadRequest("Text is required.");

        var isMatched = await IsMatchedAsync(meId, dto.ToUserId);
        if (!isMatched)
            return Forbid();

        var msg = new Message
        {
            FromUserId = meId,
            ToUserId = dto.ToUserId,
            Text = dto.Text.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();

        return Ok(new MessageDto
        {
            Id = msg.Id,
            FromUserId = msg.FromUserId,
            ToUserId = msg.ToUserId,
            Text = msg.Text,
            CreatedAtUtc = msg.CreatedAtUtc,
            ReadAtUtc = msg.ReadAtUtc
        });
    }

    // POST /messages/mark-read?otherUserId=GUID
    [HttpPost("mark-read")]
    public async Task<IActionResult> MarkRead([FromQuery] Guid otherUserId)
    {
        var meId = CurrentUserService.GetUserIdOrThrow(User);

        var isBlocked = await IsBlockedAsync(meId, otherUserId);
        if (isBlocked)
            return Forbid();

        var isMatched = await IsMatchedAsync(meId, otherUserId);
        if (!isMatched)
            return Forbid();

        var toMark = await _db.Messages
            .Where(m => m.FromUserId == otherUserId
                        && m.ToUserId == meId
                        && m.ReadAtUtc == null)
            .ToListAsync();

        if (toMark.Count == 0)
        {
            return Ok(new MarkReadResponseDto
            {
                Updated = 0
            });
        }

        var now = DateTime.UtcNow;
        foreach (var m in toMark)
            m.ReadAtUtc = now;

        await _db.SaveChangesAsync();

        return Ok(new MarkReadResponseDto
        {
            Updated = toMark.Count,
            ReadAtUtc = now
        });
    }
}