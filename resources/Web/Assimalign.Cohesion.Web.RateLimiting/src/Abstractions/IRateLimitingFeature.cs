using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// The per-exchange feature the rate-limiting middleware installs on
/// <see cref="IHttpContext.Features"/>, through which downstream stages read the limiter decision for
/// the request — the policy that made it, whether the request was admitted, and the retry-after hint
/// when it was rejected.
/// </summary>
/// <remarks>
/// <para>
/// Resolve it with <c>context.Features.Get&lt;IRateLimitingFeature&gt;()</c>. When the middleware is not
/// registered, no feature is present and the lookup returns <see langword="null"/>. When it is
/// registered but no limiter governs the request, the feature reports <see cref="IsAcquired"/> =
/// <see langword="true"/> with a <see langword="null"/> <see cref="PolicyName"/>.
/// </para>
/// <para>
/// The feature reflects the most recent decision: after the global limiter admits and a per-endpoint
/// policy is then applied, <see cref="PolicyName"/> is the endpoint policy's name. A handler only runs
/// when the request was admitted, so a handler always observes <see cref="IsAcquired"/> =
/// <see langword="true"/>; the rejected view is what the <see cref="RateLimitingOptions.OnRejected"/>
/// hook and the response path observe.
/// </para>
/// </remarks>
public interface IRateLimitingFeature : IHttpFeature
{
    /// <summary>
    /// Gets the named policy that made the current decision, or <see langword="null"/> for the global
    /// limiter, an inline endpoint policy, or when no limiter governs the request.
    /// </summary>
    string? PolicyName { get; }

    /// <summary>
    /// Gets whether the request was admitted by rate limiting (granted every lease it needed).
    /// </summary>
    bool IsAcquired { get; }

    /// <summary>
    /// Gets the retry-after hint from the rejecting lease's metadata, or <see langword="null"/> when the
    /// request was admitted or the limiter published no hint.
    /// </summary>
    TimeSpan? RetryAfter { get; }
}
