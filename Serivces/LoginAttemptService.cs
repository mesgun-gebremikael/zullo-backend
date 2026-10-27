using System.Threading.RateLimiting;

namespace Zullo.Api.Services;

/// Shared across requests. Limits all attempts, including unknown accounts and successes.
public sealed class LoginAttemptService : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public LoginAttemptService() : this(5, TimeSpan.FromMinutes(1)) { }

    internal LoginAttemptService(int permitLimit, TimeSpan window)
    {
        _limiter = PartitionedRateLimiter.Create<string, string>(email =>
            RateLimitPartition.GetFixedWindowLimiter(email.Trim().ToLowerInvariant(), _ =>
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
    }

    public bool TryAcquire(string email, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(email);
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)
            ? retry : TimeSpan.FromMinutes(1);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
