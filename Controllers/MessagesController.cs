using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Zullo.Api.Dtos;
using Zullo.Api.Services;
using System.Threading.Channels;

namespace Zullo.Api.Controllers;

[ApiController]
[Route("messages")]
[Authorize] //  kräver JWT
public class MessagesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserRelationService _userRelationService;
    private readonly PushNotificationService _pushNotificationService;
    private readonly ILogger<MessagesController> _logger;


    public MessagesController(
    AppDbContext db,
    UserRelationService userRelationService,
    PushNotificationService pushNotificationService,
    ILogger<MessagesController> logger)
    {
        _db = db;
        _userRelationService = userRelationService;
        _pushNotificationService = pushNotificationService;
        _logger = logger;
    }






    // GET /messages/thread?otherUserId=GUID
    [HttpGet("thread")]
    public async Task<IActionResult> GetThread([FromQuery] Guid otherUserId)
    {
        var meId = CurrentUserService.GetUserIdOrThrow(User);

        var isBlocked = await _userRelationService.IsBlockedAsync(meId, otherUserId);
        if (isBlocked)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorMessageResponseDto
            {
                Message = "You cannot access this conversation."
            });
        }

        var isMatched = await _userRelationService.IsMatchedAsync(meId, otherUserId);
        if (!isMatched)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorMessageResponseDto
            {
                Message = "You can only view messages with matched users."
            });
        }

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

        var isBlocked = await _userRelationService.IsBlockedAsync(meId, dto.ToUserId);
        if (isBlocked)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorMessageResponseDto
            {
                Message = "You cannot send messages in this conversation."
            });
        }


        var isMatched = await _userRelationService.IsMatchedAsync(meId, dto.ToUserId);
        if (!isMatched)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorMessageResponseDto
            {
                Message = "You can only send messages to matched users."
            });
        }

        // Anti-spam: max 1 message per 1 sekund per user
        var oneSecondAgo = DateTime.UtcNow.AddSeconds(-1);

        var recentMessage = await _db.Messages
            .Where(m => m.FromUserId == meId)
            .OrderByDescending(m => m.CreatedAtUtc)
            .FirstOrDefaultAsync();

        if (recentMessage != null && recentMessage.CreatedAtUtc > oneSecondAgo)
        {
            return BadRequest(new ErrorMessageResponseDto
            {
                Message = "You're sending messages too fast."
            });
        }

        var trimmedText = dto.Text.Trim();

        var msg = new Message
        {
            FromUserId = meId,
            ToUserId = dto.ToUserId,
            Text = trimmedText,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Messages.Add(msg);
        await _db.SaveChangesAsync();
        Console.WriteLine("SKA SKICKA PUSH NU");

        var sender = await _db.Profiles
       .AsNoTracking()
       .Where(p => p.UserId == meId)
        .Select(p => p.DisplayName)
        .FirstOrDefaultAsync();

        var receiver = await _db.Users
     .AsNoTracking()
     .Where(u => u.Id == dto.ToUserId)
     .Select(u => new { u.DeviceToken })
     .FirstOrDefaultAsync();

        _logger.LogInformation("DEVICE TOKEN CHECK: {Token}", receiver?.DeviceToken);

        if (receiver != null)
        {
            try
            {
                _logger.LogInformation("PUSH TRY START");

                await _pushNotificationService.SendMessageNotificationAsync(
     receiver.DeviceToken,
     meId.ToString(),
     string.IsNullOrWhiteSpace(sender) ? "Nytt meddelande" : sender,
     trimmedText);


                _logger.LogInformation("PUSH OK");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PUSH FAIL");
            }
        }



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

        var isBlocked = await _userRelationService.IsBlockedAsync(meId, otherUserId);
        if (isBlocked)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorMessageResponseDto
            {
                Message = "You cannot access this conversation."
            });
        }

        var isMatched = await _userRelationService.IsMatchedAsync(meId, otherUserId);
        if (!isMatched)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ErrorMessageResponseDto
            {
                Message = "You can only mark messages as read for matched users."
            });
        }

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