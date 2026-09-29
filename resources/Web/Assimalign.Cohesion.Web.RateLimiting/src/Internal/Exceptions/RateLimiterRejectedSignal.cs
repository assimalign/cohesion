using System;
using System.Threading.RateLimiting;

namespace Assimalign.Cohesion.Web.RateLimiting.Internal;

/// <summary>
/// The internal control-flow signal a per-endpoint rejection raises at the route-match seam. Cohesion's
/// router matches and dispatches in a single middleware, so there is no pipeline position between the
/// match and the handler for a policy consumer to short-circuit from; the feature-collection decorator
/// therefore throws this from the synchronous match publication, and the middleware catches it before
/// the handler runs and translates it into the rejection response. It carries the rejected lease so the
/// middleware can read its retry-after hint and hand it to the <see cref="RateLimitingOptions.OnRejected"/>
/// hook before disposing it.
/// </summary>
/// <remarks>
/// The signal is caught in the same middleware that raised it (through the router's synchronous
/// <c>SetRouteMatch</c>), so it never escapes to an outer exception boundary. The per-endpoint gate uses
/// the synchronous, non-queueing <c>AttemptAcquire</c> — queueing is a global-limiter concern (acquired
/// asynchronously up-front); an endpoint policy admits or rejects immediately.
/// </remarks>
internal sealed class RateLimiterRejectedSignal : Exception
{
    public RateLimiterRejectedSignal(RateLimitLease lease, string? policyName, TimeSpan? retryAfter)
        : base("The request was rejected by a per-endpoint rate limiting policy.")
    {
        Lease = lease;
        PolicyName = policyName;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets the rejected (non-acquired) lease.</summary>
    public RateLimitLease Lease { get; }

    /// <summary>Gets the rejecting policy name, or <see langword="null"/> for an inline policy.</summary>
    public string? PolicyName { get; }

    /// <summary>Gets the retry-after hint from the lease, or <see langword="null"/> when none was published.</summary>
    public TimeSpan? RetryAfter { get; }
}
