using System;
using System.Globalization;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RateLimiting.Internal;

/// <summary>
/// The rate-limiting middleware: acquires the global limiter's lease for every request, then the
/// published endpoint's own policy, both asynchronously with the limiter's full queueing semantics. A
/// rejection at either gate is answered with the configured status (429 by default) and a
/// <c>Retry-After</c> header from the lease metadata, with an optional
/// <see cref="RateLimitingOptions.OnRejected"/> hook that may own the response, and the rest of the
/// pipeline (the endpoint included) does not run.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint policy comes from the route match <c>UseRouting</c> publishes before this middleware
/// runs, so the middleware belongs after <c>UseRouting</c>. Once it has applied the endpoint's policy
/// (or found none) it acknowledges the endpoint. Registered ahead of <c>UseRouting</c> it sees no
/// endpoint and acknowledges none, and routing's dispatch then fails an endpoint whose
/// <see cref="RateLimitingMetadata"/> names a policy rather than running it without the limit
/// (<see cref="IRouteMiddlewareMetadata"/>). The global limiter needs no endpoint and applies in either
/// position.
/// </para>
/// <para>
/// The global limiter and the endpoint policy are additive: both must grant a lease. Acquired leases are
/// held for the whole request and released, endpoint before global, when the middleware disposes the
/// feature after the downstream pipeline completes, which is the lifetime a concurrency limiter
/// requires. The candidate endpoint of a CORS preflight never runs for the preflight, so its policy is
/// neither applied nor acknowledged.
/// </para>
/// </remarks>
internal sealed class RateLimitingMiddleware : IWebApplicationMiddleware
{
    /// <summary>
    /// The pipeline verb the middleware acknowledges on the endpoint, and the one
    /// <see cref="RateLimitingMetadata.RequiredMiddleware"/> names.
    /// </summary>
    internal const string Verb = "UseRateLimiting";

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

            if (_options.GlobalPolicy is { } global
                && !await TryAcquireAsync(context, global, policyName: null, feature).ConfigureAwait(false))
            {
                // The global limiter rejected the request; the response has been written.
                return;
            }

            if (context.GetRouteMatch() is { IsPreflight: false } endpoint)
            {
                if (endpoint.Metadata.GetMetadata<RateLimitingMetadata>() is { IsDisabled: false } metadata
                    && !await TryAcquireAsync(context, ResolvePolicy(metadata), metadata.PolicyName, feature).ConfigureAwait(false))
                {
                    // The endpoint policy rejected the request; the response has been written and the
                    // endpoint does not run.
                    return;
                }

                // Acknowledge every endpoint this middleware processed, with or without a policy of its
                // own: routing checks each metadata item that names this middleware, including a
                // group-level policy that a last-wins endpoint override replaced.
                context.AcknowledgeEndpointMiddleware(Verb);
            }

            await next.Invoke(context).ConfigureAwait(false);
        }
        finally
        {
            // Remove before disposing so later pipeline stages can never resolve a feature whose leases
            // have been released.
            context.Features.Set<IRateLimitingFeature>(null);
            feature.Dispose();
        }
    }

    // Acquires a lease from the policy, waiting in the limiter's queue when it has one. An admitted
    // lease is held on the feature until the request completes; a rejection is answered here.
    private async Task<bool> TryAcquireAsync(IHttpContext context, RateLimitingPolicy policy, string? policyName, RateLimitingFeature feature)
    {
        RateLimitLease lease = await policy.Limiter
            .AcquireAsync(context, policy.PermitCount, context.RequestCancelled)
            .ConfigureAwait(false);

        if (lease.IsAcquired)
        {
            feature.TrackLease(lease);
            feature.RecordAdmitted(policyName);
            _options.OnDecision?.Invoke(new RateLimitingDecision(policyName, true, null));
            return true;
        }

        using (lease)
        {
            TimeSpan? retryAfter = RateLimitingLeaseReader.GetRetryAfter(lease);
            feature.RecordRejected(policyName, retryAfter);
            _options.OnDecision?.Invoke(new RateLimitingDecision(policyName, false, retryAfter));
            await RejectAsync(context, lease, policyName, retryAfter).ConfigureAwait(false);
        }

        return false;
    }

    private RateLimitingPolicy ResolvePolicy(RateLimitingMetadata metadata)
    {
        if (metadata.Policy is { } inline)
        {
            return inline;
        }

        if (_options.TryGetPolicy(metadata.PolicyName!, out RateLimitingPolicy? named) && named is not null)
        {
            return named;
        }

        throw new InvalidOperationException(
            $"No rate limiting policy named '{metadata.PolicyName}' has been registered. " +
            "Register it with options.AddPolicy(name, policy) in UseRateLimiting.");
    }

    private async Task RejectAsync(IHttpContext context, RateLimitLease lease, string? policyName, TimeSpan? retryAfter)
    {
        // Both gates run before next, so the head is normally still writable here. A middleware ahead of
        // this one may already have committed it, though, and then the status can no longer be set:
        // abort the exchange at the protocol layer instead.
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
            await onRejected.Invoke(rejection, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
