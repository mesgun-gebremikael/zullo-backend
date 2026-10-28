using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Zullo.Api.Data;

namespace Zullo.Api.Services;

public sealed class DatabaseReadinessCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
            return pending.Any() ? HealthCheckResult.Unhealthy("Database upgrade required.") : HealthCheckResult.Healthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Database is unavailable.");
        }
    }
}
