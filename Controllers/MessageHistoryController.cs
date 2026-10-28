using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Dtos;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers;

[ApiController, Authorize, Route("messages")]
public class MessageHistoryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserRelationService _relations;
    public MessageHistoryController(AppDbContext db, UserRelationService relations)
    {
        _db = db;
        _relations = relations;
    }

    private sealed record Cursor([property: JsonRequired] long Sequence);
    private static readonly JsonSerializerOptions CursorJson = new(JsonSerializerDefaults.Web);
    private static string EncodeSequence(long sequence) => WebEncoders.Base64UrlEncode(
        JsonSerializer.SerializeToUtf8Bytes(new Cursor(sequence), CursorJson));
    private static string Encode(MessageDto message) => EncodeSequence(message.Sequence);

    [HttpGet("thread-page")]
    public async Task<IActionResult> GetPage([FromQuery] Guid otherUserId,
        [FromQuery, Range(1, 100)] int take = 50,
        [FromQuery, StringLength(512)] string? before = null,
        [FromQuery, StringLength(512)] string? after = null,
        CancellationToken cancellationToken = default)
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        if (await _relations.IsBlockedAsync(me, otherUserId) || !await _relations.IsMatchedAsync(me, otherUserId))
            return StatusCode(403, new ErrorMessageResponseDto { Message = "You cannot access this conversation." });
        if (before != null && after != null) return BadRequest(new ErrorMessageResponseDto { Message = "Use either before or after." });
        Cursor? cursor = null;
        if (before != null || after != null)
        {
            try
            {
                cursor = JsonSerializer.Deserialize<Cursor>(WebEncoders.Base64UrlDecode((before ?? after)!), CursorJson);
                if (cursor == null || (cursor.Sequence < 0 || (before != null && cursor.Sequence == 0)))
                    return BadRequest(new ErrorMessageResponseDto { Message = "Invalid cursor." });
            }
            catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
            {
                return BadRequest(new ErrorMessageResponseDto { Message = "Invalid cursor." });
            }
        }
        var query = _db.Messages.AsNoTracking().Where(m =>
            (m.FromUserId == me && m.ToUserId == otherUserId) ||
            (m.FromUserId == otherUserId && m.ToUserId == me));
        if (cursor != null)
        {
            var sequence = cursor.Sequence;
            query = after != null ? query.Where(m => m.DeliverySequence > sequence)
                : query.Where(m => m.DeliverySequence < sequence);
        }
        var ordered = after != null ? query.OrderBy(m => m.DeliverySequence)
            : query.OrderByDescending(m => m.DeliverySequence);
        var rows = await ordered.Take(take + 1).Select(m => new MessageDto
        {
            Id = m.Id, Sequence = m.DeliverySequence, FromUserId = m.FromUserId, ToUserId = m.ToUserId,
            Text = m.Text, ClientMessageId = m.ClientMessageId,
            CreatedAtUtc = m.CreatedAtUtc, ReadAtUtc = m.ReadAtUtc
        }).ToListAsync(cancellationToken);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        if (after == null) rows.Reverse();
        var receipt = await _db.Messages.AsNoTracking()
            .Where(m => m.FromUserId == me && m.ToUserId == otherUserId && m.ReadAtUtc != null)
            .OrderByDescending(m => m.DeliverySequence)
            .Select(m => new { m.DeliverySequence, m.ReadAtUtc }).FirstOrDefaultAsync(cancellationToken);
        return Ok(new MessagePageDto
        {
            Messages = rows, HasMore = more,
            OutgoingReadThrough = receipt?.DeliverySequence, OutgoingReadAtUtc = receipt?.ReadAtUtc,
            NextCursor = more ? Encode(after != null ? rows[^1] : rows[0]) : null,
            LatestCursor = rows.Count > 0 ? Encode(rows[^1]) : after ?? EncodeSequence(0)
        });
    }
}
