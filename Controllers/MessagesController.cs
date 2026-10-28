using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Zullo.Api.Data;
using Zullo.Api.Models;
using Microsoft.AspNetCore.SignalR;
using Zullo.Api.Hubs;
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
    private readonly IHubContext<ChatHub> _chatHub;
    private readonly MessageSendLimiter _sendLimiter;



    public MessagesController(
     AppDbContext db,
     UserRelationService userRelationService,
     PushNotificationService pushNotificationService,
     ILogger<MessagesController> logger,
     IHubContext<ChatHub> chatHub,
     MessageSendLimiter sendLimiter)
    {
        _db = db;
        _userRelationService = userRelationService;
        _pushNotificationService = pushNotificationService;
        _logger = logger;
        _chatHub = chatHub;
        _sendLimiter = sendLimiter;
    }







    // GET /messages/thread?otherUserId=GUID
    [HttpGet("thread")]
    public async Task<IActionResult> GetThread([FromQuery] Guid otherUserId)
    {
        if (!CurrentUserService.TryGetUserId(User, out var meId)) return Unauthorized();

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
            .OrderBy(m => m.DeliverySequence)
           .Select(m => new MessageDto
           {
               Id = m.Id,
               Sequence = m.DeliverySequence,
               FromUserId = m.FromUserId,
               ToUserId = m.ToUserId,
               Text = m.Text,
               CreatedAtUtc = m.CreatedAtUtc,
               ReadAtUtc = m.ReadAtUtc,
               ClientMessageId = m.ClientMessageId
           })

            .ToListAsync();

        return Ok(msgs);
    }


    // POST /messages/send
    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendMessageDto dto)
    {
        if (!CurrentUserService.TryGetUserId(User, out var meId)) return Unauthorized();

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

        await using var deliveryTransaction = _db.Database.IsNpgsql()
            ? await _db.Database.BeginTransactionAsync() : null;
        if (deliveryTransaction != null)
        {
            var pair = string.CompareOrdinal(meId.ToString(), dto.ToUserId.ToString()) < 0
                ? $"{meId}:{dto.ToUserId}" : $"{dto.ToUserId}:{meId}";
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({pair}, 0))");
            if (await _userRelationService.IsBlockedAsync(meId, dto.ToUserId) ||
                !await _userRelationService.IsMatchedAsync(meId, dto.ToUserId))
                return StatusCode(403, new ErrorMessageResponseDto { Message = "You cannot access this conversation." });
        }

        var existing = await FindDelivery(meId, dto.ToUserId, dto.ClientMessageId);
        if (existing != null) return Replay(existing, dto.Text.Trim());
        if (!_sendLimiter.TryAcquire(meId, out var retryAfter))
        {
            Response.Headers.RetryAfter = AuthenticationProtection.RetrySeconds(retryAfter);
            return StatusCode(429, new ErrorMessageResponseDto { Message = "You're sending messages too fast." });
        }

        var trimmedText = dto.Text.Trim();

        var msg = new Message
        {
            FromUserId = meId,
            ToUserId = dto.ToUserId,
            Text = trimmedText,
            ClientMessageId = dto.ClientMessageId,
            DeliveryKey = dto.ClientMessageId,
            CreatedAtUtc = DateTime.UtcNow
        };


        _db.Messages.Add(msg);
        try
        {
            await _db.SaveChangesAsync();
            if (deliveryTransaction != null) await deliveryTransaction.CommitAsync();
        }
        catch (DbUpdateException)
        {
            if (deliveryTransaction != null) await deliveryTransaction.RollbackAsync();
            _db.Entry(msg).State = EntityState.Detached;
            var winner = await FindDelivery(meId, dto.ToUserId, dto.ClientMessageId);
            if (winner != null) return Replay(winner, trimmedText);
            throw;
        }

        try
        {
        await _chatHub.Clients.Users(meId.ToString(), dto.ToUserId.ToString()).SendAsync(
     "MessageReceived",
     new
     {
         id = msg.Id,
         sequence = msg.DeliverySequence,
         fromUserId = meId,
         toUserId = dto.ToUserId,
         text = trimmedText,
         createdAtUtc = msg.CreatedAtUtc,
         clientMessageId = msg.ClientMessageId
     });


        }
        catch (Exception exception)
        {
            // Persistence already succeeded. Reconnection/history repairs a missed live event.
            _logger.LogWarning(exception, "Live delivery failed for message {MessageId}", msg.Id);
        }


        var senderProfile = await _db.Profiles
    .AsNoTracking()
    .Where(p => p.UserId == meId)
    .Select(p => new
    {
        p.DisplayName,
        p.PhotoUrls
    })
    .FirstOrDefaultAsync();

        var sender = senderProfile?.DisplayName;
        var senderPhotoUrl = senderProfile?.PhotoUrls?.FirstOrDefault();

        var receiver = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == dto.ToUserId)
            .Select(u => new { u.DeviceToken })
            .FirstOrDefaultAsync();



        if (!string.IsNullOrWhiteSpace(receiver?.DeviceToken))
        {
            try
            {
                using var pushTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

                await _pushNotificationService.SendMessageNotificationAsync(
                    receiver.DeviceToken,
                    meId.ToString(),
                    string.IsNullOrWhiteSpace(sender) ? "Nytt meddelande" : sender,
                    trimmedText,
                    senderPhotoUrl,
                    dto.ToUserId.ToString(), pushTimeout.Token);

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
            Sequence = msg.DeliverySequence,
            FromUserId = msg.FromUserId,
            ToUserId = msg.ToUserId,
            Text = msg.Text,
            CreatedAtUtc = msg.CreatedAtUtc,
            ReadAtUtc = msg.ReadAtUtc,
            ClientMessageId = msg.ClientMessageId
        });

    }

    // POST /messages/mark-read?otherUserId=GUID
    [HttpPost("mark-read")]
    public async Task<IActionResult> MarkRead([FromQuery] Guid otherUserId)
    {
        if (!CurrentUserService.TryGetUserId(User, out var meId)) return Unauthorized();

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

        var cutoff = await _db.Messages.AsNoTracking()
            .Where(m => m.FromUserId == otherUserId && m.ToUserId == meId && m.ReadAtUtc == null)
            .MaxAsync(m => (long?)m.DeliverySequence);
        if (cutoff == null) return Ok(new MarkReadResponseDto { Updated = 0 });
        var now = DateTime.UtcNow;
        var updated = await _db.Messages
            .Where(m => m.FromUserId == otherUserId && m.ToUserId == meId && m.ReadAtUtc == null && m.DeliverySequence <= cutoff.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.ReadAtUtc, now));

        try
        {
        await _chatHub.Clients.User(otherUserId.ToString()).SendAsync(
            "MessagesRead",
            new
            {
                chatUserId = meId,
                readAtUtc = now,
                readThroughSequence = cutoff.Value
            });

        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Live read receipt failed");
        }
        return Ok(new MarkReadResponseDto
        {
            Updated = updated,
            ReadThroughSequence = cutoff,
            ReadAtUtc = now
        });

    }
    private Task<Message?> FindDelivery(Guid sender, Guid recipient, string key) =>
        _db.Messages.AsNoTracking().Where(m => m.FromUserId == sender && m.ToUserId == recipient && (m.DeliveryKey == key || (m.DeliveryKey == null && m.ClientMessageId == key)))
            .OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.Id).FirstOrDefaultAsync();

    private IActionResult Replay(Message message, string text) => message.Text != text
        ? Conflict(new ErrorMessageResponseDto { Message = "This message identifier has already been used for different content." })
        : Ok(new MessageDto
        {
            Id = message.Id, Sequence = message.DeliverySequence, FromUserId = message.FromUserId, ToUserId = message.ToUserId,
            Text = message.Text, ClientMessageId = message.ClientMessageId,
            CreatedAtUtc = message.CreatedAtUtc, ReadAtUtc = message.ReadAtUtc
        });
}