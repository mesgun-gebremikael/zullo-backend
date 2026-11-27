using Microsoft.EntityFrameworkCore;
using Zullo.Api.Data;

namespace Zullo.Api.Services;

// Webhooks are primary. Include recently expired subscriptions so a missed renewal
// webhook does not prevent periodic recovery; no rights are granted beyond verified expiry.
public class CommerceReconciliation(IServiceScopeFactory scopes, IConfiguration config, ILogger<CommerceReconciliation> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!config.GetValue<bool>("Commerce:Enabled")) continue;
            try
            {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cutoff = DateTime.UtcNow.AddMinutes(-2);
            var users = await db.CommerceAccounts.AsNoTracking()
                .Where(a => (a.PremiumUntilUtc > DateTime.UtcNow.AddDays(-7) || a.ActivityPrivacyUntilUtc > DateTime.UtcNow.AddDays(-7)) && (a.LastVerifiedUtc == null || a.LastVerifiedUtc < cutoff))
                .OrderBy(a => a.LastVerifiedUtc).Select(a => a.UserId).Take(100).ToListAsync(stoppingToken);
            foreach (var user in users)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    using var accountScope = scopes.CreateScope();
                    await accountScope.ServiceProvider.GetRequiredService<CommerceService>().State(user, force: true, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                { logger.LogWarning(exception, "Subscription reconciliation will retry."); }
            }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            { logger.LogWarning(exception, "Subscription reconciliation is temporarily unavailable and will retry."); }
        }
    }
}
