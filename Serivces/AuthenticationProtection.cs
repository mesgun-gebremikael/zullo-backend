using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Zullo.Api.Dtos;

namespace Zullo.Api.Services;

public static class AuthenticationProtection
{
    public const string Policy = "authentication";

    public static IServiceCollection AddAuthenticationProtection(this IServiceCollection services)
    {
        services.AddSingleton<LoginAttemptService>();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                // Do not trust a client-supplied X-Forwarded-For header.
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                    ? value : TimeSpan.FromMinutes(1);
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = RetrySeconds(retry);
                await context.HttpContext.Response.WriteAsJsonAsync(new ErrorMessageResponseDto
                {
                    Message = "Too many authentication attempts. Try again later."
                }, cancellationToken);
            };
        });
        return services;
    }

    public static string RetrySeconds(TimeSpan retry) =>
        Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
