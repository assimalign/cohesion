using System;
using System.Globalization;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RateLimiting.Internal;

/// <summary>
/// The rate-limiting middleware: acquires the global limiter's lease up-front (with the limiter's full
/// queueing semantics) for every request, then hands downstream a context that gates matched endpoints
/// against their per-endpoint policy at the route-match seam. A rejection at either point is answered
/// with the configured status (429 by default) and a <c>Retry-After</c> header from the lease metadata,
/// with an optional <see cref="RateLimitingOptions.OnRejected"/> hook that may own the response.
/// </summary>
/// <remarks>
/// <para>
/// The global limiter and any per-endpoint policy are additive — both must grant a lease — because the
/// global lease is acquired before routing identifies the endpoint, so it cannot be retroactively
/// skipped. Acquired leases are held for the whole request and released when the middleware disposes the
/// feature, which is the lifetime a concurrency limiter requires.
/// </para>
/// <para>
/// The per-endpoint gate raises a <see cref="RateLimiterRejectedSignal"/> from the synchronous route-match
/// publication; this middleware catches it (the signal never escapes) and writes the rejection before the
/// handler runs.
/// </para>
/// </remarks>
internal sealed class RateLimitingMiddleware : IWebApplicationMiddleware
{
    private readonly RateLimitingOptions _options;

    public RateLimitingMiddleware(RateLimitingOptions options)
    {
        _options = options;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        RateLimitingFeature feature = new();

        try
        {
            context.Features.Set<IRateLimitingFeature>(feature);

            if (_options.GlobalPolicy is { } global && !await TryAcquireGlobalAsync(context, global, feature).ConfigureAwait(false))
            {
                // Global limiter rejected the request; the response has been written.
                return;
            }

            try
            {
                await next.Invoke(new RateLimitingHttpContext(context, feature, _options)).ConfigureAwait(false);
            }
            catch (RateLimiterRejectedSignal signal)
            {
                using (signal.Lease)
                {
                    _options.OnDecision?.Invoke(new RateLimitingDecision(signal.PolicyName, false, signal.RetryAfter));
                    await RejectAsync(context, signal.Lease, signal.PolicyName, signal.RetryAfter).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Remove before disposing so later pipeline stages can never resolve a feature whose leases
            // have been released.
            context.Features.Set<IRateLimitingFeature>(null);
            feature.Dispose();
        }
    }

    private async Task<bool> TryAcquireGlobalAsync(IHttpContext context, RateLimitingPolicy policy, RateLimitingFeature feature)
    {
        RateLimitLease lease = await policy.Limiter
            .AcquireAsync(context, policy.PermitCount, context.RequestCancelled)
            .ConfigureAwait(false);

        if (lease.IsAcquired)
        {
            feature.TrackLease(lease);
            feature.RecordAdmitted(null);
            _options.OnDecision?.Invoke(new RateLimitingDecision(null, true, null));
            return true;
        }

        using (lease)
        {
            TimeSpan? retryAfter = RateLimitingLeaseReader.GetRetryAfter(lease);
            feature.RecordRejected(null, retryAfter);
            _options.OnDecision?.Invoke(new RateLimitingDecision(null, false, retryAfter));
            await RejectAsync(context, lease, policyName: null, retryAfter).ConfigureAwait(false);
        }

        return false;
    }

    private async Task RejectAsync(IHttpContext context, RateLimitLease lease, string? policyName, TimeSpan? retryAfter)
    {
        // If a downstream stage already committed the response head, the status can no longer be set —
        // abort the exchange at the protocol layer instead. Global rejection is pre-next so this never
        // trips there; the guard covers the per-endpoint seam.
        if (context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true })
        {
            await context.CancelAsync().ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = _options.RejectionStatusCode;

        if (retryAfter is { } delay)
        {
            long seconds = (long)Math.Ceiling(delay.TotalSeconds);
            if (seconds < 0)
            {
                seconds = 0;
            }

            context.Response.Headers[HttpHeaderKey.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);
        }

        if (_options.OnRejected is { } onRejected)
        {
            RateLimitingRejectionContext rejection = new(context, lease, policyName, retryAfter, _options.RejectionStatusCode);

            // The request token may be cancelled; the rejection write must not observe it or the answer
            // would cancel itself.
            await onRejected.Invoke(rejection, System.Threading.CancellationToken.None).ConfigureAwait(false);
        }
    }
}
