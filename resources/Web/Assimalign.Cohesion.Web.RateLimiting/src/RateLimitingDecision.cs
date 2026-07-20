using System;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// An immutable record of one limiter decision, handed to the
/// <see cref="RateLimitingOptions.OnDecision"/> observation hook each time a request is evaluated
/// against a limiter (the global limiter, then any per-endpoint policy). It is the lightweight,
/// dependency-free telemetry seam this package offers in place of a hosting-layer metrics integration
/// (which a feature package cannot reach; see the package DESIGN.md).
/// </summary>
public readonly struct RateLimitingDecision
{
    /// <summary>
    /// Creates a decision record.
    /// </summary>
    /// <param name="policyName">
    /// The named policy that made the decision, or <see langword="null"/> for the global limiter or an
    /// inline (unnamed) endpoint policy.
    /// </param>
    /// <param name="isAcquired"><see langword="true"/> when the limiter granted a lease; otherwise <see langword="false"/>.</param>
    /// <param name="retryAfter">The retry-after hint from the lease metadata when the request was rejected; otherwise <see langword="null"/>.</param>
    public RateLimitingDecision(string? policyName, bool isAcquired, TimeSpan? retryAfter)
    {
        PolicyName = policyName;
        IsAcquired = isAcquired;
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the named policy that made the decision, or <see langword="null"/> for the global limiter or
    /// an inline endpoint policy.
    /// </summary>
    public string? PolicyName { get; }

    /// <summary>
    /// Gets whether the limiter granted a lease.
    /// </summary>
    public bool IsAcquired { get; }

    /// <summary>
    /// Gets the retry-after hint from the rejecting lease's metadata, or <see langword="null"/> when the
    /// request was admitted or the limiter published no hint.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
