using Microsoft.AspNetCore.RateLimiting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;
using Zullo.Api.Services;

namespace Zullo.Api.Controllers;

public record PrivacyUpdate(bool HideActivity, bool Incognito);

[ApiController, Authorize, Route("commerce"), EnableRateLimiting("commerce")]
public class CommerceController(AppDbContext db, CommerceService commerce, IConfiguration configuration, IHostEnvironment environment, Microsoft.AspNetCore.SignalR.IHubContext<Zullo.Api.Hubs.ChatHub> hub, ILogger<CommerceController> logger) : ControllerBase
{
    [HttpGet("state")]
    public async Task<IActionResult> State(CancellationToken cancellation) =>
        CurrentUserService.TryGetUserId(User, out var user) ? Ok(await commerce.State(user, cancellation: cancellation)) : Unauthorized();

    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken cancellation)
    {
        if (!CurrentUserService.TryGetUserId(User, out var user)) return Unauthorized();
        try { return Ok(await commerce.State(user, force: true, cancellation)); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        { return StatusCode(503, new { message = "Köpet kunde inte verifieras ännu. Ditt saldo ändras först efter verifiering. Försök igen." }); }
    }

    [HttpPost("boost")]
    public async Task<IActionResult> Boost(CancellationToken cancellation)
    {
        if (!CurrentUserService.TryGetUserId(User, out var user)) return Unauthorized();
        await commerce.State(user, cancellation: cancellation);
        await using var transaction = await commerce.Begin(user);
        var account = await commerce.Account(user);
        if (!await db.Profiles.AnyAsync(p => p.UserId == user && p.IsVisible, cancellation))
            return Conflict(new { message = "Slutför din profil med minst två bilder innan du aktiverar Boost." });
        if (account.BoostUntilUtc > DateTime.UtcNow) return Conflict(new { message = "En Boost är redan aktiv." });
        if (!CommerceService.ConsumeBoost(account)) return StatusCode(402, new { message = "Du har inga Boosts kvar." });
        account.BoostUntilUtc = DateTime.UtcNow.AddMinutes(30);
        await db.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        return Ok(commerce.ToState(account));
    }

    [HttpPatch("privacy")]
    public async Task<IActionResult> Privacy(PrivacyUpdate update, CancellationToken cancellation)
    {
        if (!CurrentUserService.TryGetUserId(User, out var user)) return Unauthorized();
        await commerce.State(user, cancellation: cancellation);
        await using var transaction = await commerce.Begin(user);
        var account = await commerce.Account(user);
        if ((update.HideActivity && !CommerceService.CanHideActivity(account)) || (update.Incognito && !CommerceService.Premium(account)))
            return StatusCode(402, new { message = "Dölj aktivitet kräver eget abonnemang eller Premium. Inkognito kräver Premium." });
        account.HideActivity = update.HideActivity;
        account.Incognito = update.Incognito;
        await db.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
        return Ok(commerce.ToState(account));
    }

    // RevenueCat must use this exact configured Authorization header. No client-supplied grants.
    [AllowAnonymous, HttpPost("webhook"), RequestSizeLimit(65536)]
    public async Task<IActionResult> Webhook(JsonElement payload, CancellationToken cancellation)
    {
        var secret = configuration["Commerce:WebhookAuthorization"] ?? "";
        var supplied = Request.Headers.Authorization.ToString();
        if (!configuration.GetValue<bool>("Commerce:Enabled") || secret.Length < 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
            return Unauthorized();
        if (!payload.TryGetProperty("event", out var evt)) return BadRequest();
        var ids = new HashSet<Guid>();
        foreach (var field in new[] { "app_user_id", "original_app_user_id" })
            if (evt.TryGetProperty(field, out var id) && Guid.TryParse(id.GetString(), out var user)) ids.Add(user);
        foreach (var field in new[] { "transferred_from", "transferred_to" })
            if (evt.TryGetProperty(field, out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var id in array.EnumerateArray()) if (Guid.TryParse(id.GetString(), out var user)) ids.Add(user);
        foreach (var user in ids)
        {
            if (!await db.Users.AnyAsync(u => u.Id == user, cancellation)) continue;
            // Refetch authoritative state; webhook subscription dates/roles are never trusted.
            await commerce.State(user, force: true, cancellation);
            if (evt.TryGetProperty("type", out var type) && type.GetString() is "NON_RENEWING_PURCHASE" or "CANCELLATION" &&
                evt.TryGetProperty("transaction_id", out var txn) && evt.TryGetProperty("store", out var store) &&
                evt.TryGetProperty("product_id", out var product) && evt.TryGetProperty("environment", out var purchaseEnvironment))
            {
                var allowedEnvironment = purchaseEnvironment.GetString() == "PRODUCTION" ||
                    (purchaseEnvironment.GetString() == "SANDBOX" && !environment.IsProduction() && configuration.GetValue<bool>("Commerce:AllowSandbox"));
                var normalizedStore = store.GetString()!.ToLowerInvariant();
                var productId = product.GetString()!.Split(':')[0];
                var amounts = RevenueCatVerifier.ProductAmounts(productId);
                var id = txn.GetString();
                if (allowedEnvironment && normalizedStore is "app_store" or "play_store" && amounts != (0, 0) &&
                    !string.IsNullOrWhiteSpace(id) && id.Length <= 180)
                    await commerce.ApplyCredit(user, new VerifiedCredit($"{normalizedStore}:{id}", productId, amounts.Item1, amounts.Item2),
                        refund: type.GetString() == "CANCELLATION", cancellation);
            }
            try { await Microsoft.AspNetCore.SignalR.ClientProxyExtensions.SendAsync(hub.Clients.User(user.ToString()), "CommerceChanged", cancellation); }
            catch (Exception exception) { logger.LogWarning(exception, "Commerce notification will reconcile on resume."); }
        }
        return Ok();
    }
}

[ApiController, Authorize, Route("presence")]
public class PresenceController(AppDbContext db, CommerceService commerce, UserRelationService relations) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Heartbeat()
    {
        if (!CurrentUserService.TryGetUserId(User, out var user)) return Unauthorized();
        await commerce.RefreshBenefitsIfSubscribed(user);
        await using var transaction = await commerce.Begin(user);
        var account = await commerce.Account(user);
        account.LastActivityUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return NoContent();
    }
    [HttpGet("{peer:guid}")]
    public async Task<IActionResult> Get(Guid peer)
    {
        if (!CurrentUserService.TryGetUserId(User, out var user)) return Unauthorized();
        if (await relations.IsBlockedAsync(user, peer) || !await relations.IsMatchedAsync(user, peer)) return StatusCode(403);
        var account = await db.CommerceAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == peer);
        var last = CommerceService.CanHideActivity(account) && account!.HideActivity ? null : account?.LastActivityUtc;
        return Ok(new { lastActivityUtc = last, online = last > DateTime.UtcNow.AddSeconds(-90) });
    }
}
