using System;
using System.Threading.RateLimiting;

namespace Assimalign.Cohesion.Web.RateLimiting.Internal;

/// <summary>
/// Reads the standard retry-after hint from a <see cref="RateLimitLease"/>. The BCL window and
/// token-bucket limiters publish <see cref="MetadataName.RetryAfter"/> on a rejected lease; the
/// concurrency limiter does not, so the hint is genuinely optional.
/// </summary>
internal static class RateLimitingLeaseReader
{
    public static TimeSpan? GetRetryAfter(RateLimitLease lease)
        => lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter) ? retryAfter : null;
}
