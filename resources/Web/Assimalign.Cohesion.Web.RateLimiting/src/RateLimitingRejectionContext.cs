using System;
using System.Threading.RateLimiting;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RateLimiting;

/// <summary>
/// The context handed to the <see cref="RateLimitingOptions.OnRejected"/> hook when a request is
/// rejected by a limiter. The hook may write its own response on <see cref="Context"/>; if it does
/// not, the middleware's default answer stands — the configured status
/// (<see cref="RateLimitingOptions.RejectionStatusCode"/>, 429 by default) plus a
/// <c>Retry-After</c> header when the lease supplied one, with no body (which composes with the
/// status-code-pages middleware).
/// </summary>
/// <remarks>
/// The middleware sets the status code and <c>Retry-After</c> header <em>before</em> invoking the hook,
/// so the hook observes and may override them. The rejected <see cref="Lease"/> is live for the
/// duration of the call and is disposed by the middleware afterward — do not retain it.
/// </remarks>
public sealed class RateLimitingRejectionContext
{
    internal RateLimitingRejectionContext(
        IHttpContext context,
        RateLimitLease lease,
        string? policyName,
        TimeSpan? retryAfter,
        HttpStatusCode statusCode)
    {
        Context = context;
        Lease = lease;
        PolicyName = policyName;
        RetryAfter = retryAfter;
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the HTTP exchange that was rejected.
    /// </summary>
    public IHttpContext Context { get; }

    /// <summary>
    /// Gets the rejected (non-acquired) lease. Live for the duration of the hook only; the middleware
    /// disposes it afterward.
    /// </summary>
    public RateLimitLease Lease { get; }

    /// <summary>
    /// Gets the named policy that rejected the request, or <see langword="null"/> for the global limiter
    /// or an inline endpoint policy.
    /// </summary>
    public string? PolicyName { get; }

    /// <summary>
    /// Gets the retry-after hint from the lease metadata, or <see langword="null"/> when none was published.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Gets the status code the middleware set on the response before invoking the hook.
    /// </summary>
    public HttpStatusCode StatusCode { get; }
}
