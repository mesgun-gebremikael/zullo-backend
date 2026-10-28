using System.Threading.RateLimiting;

namespace Zullo.Api.Services;

public sealed class MessageSendLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter = PartitionedRateLimiter.Create<Guid, Guid>(user =>
        RateLimitPartition.GetFixedWindowLimiter(user, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 3, Window = TimeSpan.FromSeconds(2), QueueLimit = 0, AutoReplenishment = true
        }));

    public bool TryAcquire(Guid user, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(user);
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromSeconds(2);
        return lease.IsAcquired;
    }
    public void Dispose() => _limiter.Dispose();
}
