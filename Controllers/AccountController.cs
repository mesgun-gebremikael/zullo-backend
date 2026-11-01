using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Hubs;
using Zullo.Api.Models;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers;

public record PauseAccount(bool Paused);
public record RemoveAccount([Required, StringLength(128)] string Password, bool Confirm);

[ApiController, Authorize, Route("me/account"), EnableRateLimiting("commerce")]
public class AccountController(AppDbContext db, IHubContext<ChatHub> hub, ILogger<AccountController> logger, LoginAttemptService attempts) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> State()
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == me && !u.IsClosed);
        return account == null ? Unauthorized() : Ok(new { paused = account.IsPaused });
    }

    [HttpPatch("pause")]
    public async Task<IActionResult> Pause(PauseAccount update)
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        await using var transaction = await Begin(me);
        var account = await db.Users.SingleOrDefaultAsync(u => u.Id == me && !u.IsClosed);
        if (account == null) return Unauthorized();
        account.IsPaused = update.Paused;
        var recipients = await db.Likes.Where(l => l.FromUserId == me).Select(l => l.ToUserId).Distinct().ToListAsync();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await NotifyOwner(me, "AccountStateChanged", new { paused = account.IsPaused });
        await NotifyLikes(recipients);
        return Ok(new { paused = account.IsPaused });
    }

    [HttpPost("removal"), EnableRateLimiting(AuthenticationProtection.Policy)]
    public async Task<IActionResult> Removal(RemoveAccount request)
    {
        if (!CurrentUserService.TryGetUserId(User, out var me)) return Unauthorized();
        if (!request.Confirm) return BadRequest(new { message = "Bekräfta att du vill stänga kontot och begära radering." });
        if (!attempts.TryAcquire($"account-removal:{me}", out var retry)) {
            Response.Headers.RetryAfter = AuthenticationProtection.RetrySeconds(retry);
            return StatusCode(429, new { message = "För många försök. Vänta en stund och försök igen." });
        }
        await using var transaction = await Begin(me);
        var account = await db.Users.SingleOrDefaultAsync(u => u.Id == me && !u.IsClosed);
        if (account == null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(account.PasswordHash))
            return Conflict(new { message = "Kontot behöver bekräftas med din inloggningsmetod innan radering." });
        if (!BCrypt.Net.BCrypt.Verify(request.Password, account.PasswordHash))
            return BadRequest(new { message = "Lösenordet stämmer inte. Kontot har inte ändrats." });
        var profile = await db.Profiles.SingleOrDefaultAsync(p => p.UserId == me);
        var removal = new AccountRemovalRequest {
            FormerUserId = me, PhotoUrls = profile?.PhotoUrls.Distinct().ToList() ?? new(),
            ExternalAssetsReviewed = profile == null || profile.PhotoUrls.Count == 0
        };
        db.AccountRemovalRequests.Add(removal);
        var recipients = await db.Likes.Where(l => l.FromUserId == me).Select(l => l.ToUserId).Distinct().ToListAsync();
        var peers = await db.Matches.Where(m => m.UserAId == me || m.UserBId == me)
            .Select(m => m.UserAId == me ? m.UserBId : m.UserAId).ToListAsync();
        if (profile != null) db.Profiles.Remove(profile);
        db.Messages.RemoveRange(await db.Messages.Where(m => m.FromUserId == me || m.ToUserId == me).ToListAsync());
        db.Matches.RemoveRange(await db.Matches.Where(m => m.UserAId == me || m.UserBId == me).ToListAsync());
        db.Likes.RemoveRange(await db.Likes.Where(l => l.FromUserId == me || l.ToUserId == me).ToListAsync());
        db.Skips.RemoveRange(await db.Skips.Where(s => s.FromUserId == me || s.ToUserId == me).ToListAsync());
        db.Blocks.RemoveRange(await db.Blocks.Where(b => b.FromUserId == me || b.BlockedUserId == me).ToListAsync());
        // Retain only a pseudonymous account key for payment replay protection and safety reports.
        account.IsClosed = true; account.IsPaused = true; account.ClosedAtUtc = DateTime.UtcNow;
        account.Email = null; account.PhoneNumber = null; account.GoogleSubject = null;
        account.PasswordHash = ""; account.DeviceToken = null; account.IsVerified = false;
        var commerce = await db.CommerceAccounts.SingleOrDefaultAsync(a => a.UserId == me);
        if (commerce != null) {
            commerce.Incognito = false; commerce.HideActivity = false; commerce.BoostUntilUtc = null;
            commerce.LastActivityUtc = null;
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await NotifyOwner(me, "AccountClosed", new { requestId = removal.Id, cleanupPending = !removal.ExternalAssetsReviewed });
        await NotifyLikes(recipients);
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await hub.Clients.Users(peers.Select(p => p.ToString())).SendAsync("RelationChanged", new { otherUserId = me }, timeout.Token);
        } catch (Exception ex) { logger.LogWarning(ex, "Account closure notification failed; clients reconcile on refresh."); }
        return Accepted(new { requestId = removal.Id, cleanupPending = !removal.ExternalAssetsReviewed,
            message = "Kontot är stängt och din raderingsbegäran är registrerad. Externa bilder behandlas separat." });
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> Begin(Guid me)
    {
        var transaction = await db.Database.BeginTransactionAsync();
        await AccountLifecycleLock.Exclusive(db, me);
        if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"User\" WHERE \"Id\" = {me} FOR UPDATE");
        return transaction;
    }
    private async Task NotifyLikes(List<Guid> recipients)
    {
        if (recipients.Count == 0) return;
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await hub.Clients.Users(recipients.Select(id => id.ToString())).SendAsync("LikesChanged", new { }, timeout.Token);
        } catch (Exception ex) { logger.LogWarning(ex, "Account visibility notification failed."); }
    }
    private async Task NotifyOwner(Guid me, string name, object payload)
    {
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await hub.Clients.User(me.ToString()).SendAsync(name, payload, timeout.Token);
        } catch (Exception ex) { logger.LogWarning(ex, "Account state notification failed."); }
    }
}
