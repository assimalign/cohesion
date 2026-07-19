using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.RateLimiting;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RateLimiting.Internal;

/// <summary>
/// A pass-through <see cref="IHttpFeatureCollection"/> decorator that observes the router publishing
/// its route match (<see cref="IRouteMatchFeature"/>) and applies the matched endpoint's
/// <see cref="RateLimitingMetadata"/> at that moment — after route selection, before the endpoint's
/// handler runs.
/// </summary>
/// <remarks>
/// Cohesion's router matches and dispatches in a single middleware, so there is no pipeline position
/// "between match and handler" for a policy consumer to occupy. The documented routing contract — the
/// router installs the match on <see cref="IHttpContext.Features"/> before invoking the handler — is
/// therefore the seam: the endpoint policy is acquired synchronously here, and a rejection is raised as
/// a <see cref="RateLimiterRejectedSignal"/> so the middleware can answer before the handler runs. All
/// other members forward untouched.
/// </remarks>
internal sealed class RateLimitingFeatureCollection : IHttpFeatureCollection
{
    private readonly IHttpFeatureCollection _inner;
    private readonly IHttpContext _context;
    private readonly RateLimitingFeature _feature;
    private readonly RateLimitingOptions _options;
    private bool _applied;

    public RateLimitingFeatureCollection(
        IHttpFeatureCollection inner,
        IHttpContext context,
        RateLimitingFeature feature,
        RateLimitingOptions options)
    {
        _inner = inner;
        _context = context;
        _feature = feature;
        _options = options;
    }

    public int Version => _inner.Version;

    public IHttpFeature? Get(string name) => _inner.Get(name);

    public void Set(IHttpFeature? feature)
    {
        _inner.Set(feature);

        // Apply at most once per exchange: the endpoint gate acquires a lease, and re-applying on a
        // re-published match would leak a second one.
        if (!_applied && feature is IRouteMatchFeature match)
        {
            _applied = true;
            ApplyEndpointPolicy(match);
        }
    }

    public bool Remove(string name) => _inner.Remove(name);

    public IEnumerator<IHttpFeature> GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void ApplyEndpointPolicy(IRouteMatchFeature match)
    {
        RateLimitingMetadata? metadata = match.Metadata.GetMetadata<RateLimitingMetadata>();

        if (metadata is null || metadata.IsDisabled)
        {
            return;
        }

        RateLimitingPolicy policy = ResolvePolicy(metadata);

        // Synchronous, non-queueing acquire: the sync route-match seam cannot await, so queueing stays a
        // global-limiter concern. AttemptAcquire admits or rejects immediately.
        RateLimitLease lease = policy.Limiter.AttemptAcquire(_context, policy.PermitCount);
        TimeSpan? retryAfter = RateLimitingLeaseReader.GetRetryAfter(lease);

        _options.OnDecision?.Invoke(new RateLimitingDecision(metadata.PolicyName, lease.IsAcquired, retryAfter));

        if (lease.IsAcquired)
        {
            _feature.TrackLease(lease);
            _feature.RecordAdmitted(metadata.PolicyName);
            return;
        }

        _feature.RecordRejected(metadata.PolicyName, retryAfter);
        throw new RateLimiterRejectedSignal(lease, metadata.PolicyName, retryAfter);
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
}
