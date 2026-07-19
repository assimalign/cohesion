using System.Collections;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Internal;

/// <summary>
/// A pass-through <see cref="IHttpFeatureCollection"/> decorator that observes the router
/// publishing its route match (<see cref="IRouteMatchFeature"/>) and applies the matched
/// endpoint's <see cref="RequestTimeoutMetadata"/> to the exchange's timeout engine at that
/// moment — after route selection, before the endpoint's handler runs.
/// </summary>
/// <remarks>
/// Cohesion's router matches and dispatches in a single middleware, so there is no pipeline
/// position "between match and handler" for a policy consumer to occupy. The documented routing
/// contract — the router installs the match on <see cref="IHttpContext.Features"/> before
/// invoking the handler — is therefore the seam: every installation funnels through the
/// name-keyed <see cref="Set"/>, and the decorator reacts to the one feature type it cares about.
/// All other members forward untouched.
/// </remarks>
internal sealed class RequestTimeoutFeatureCollection : IHttpFeatureCollection
{
    private readonly IHttpFeatureCollection _inner;
    private readonly RequestTimeoutFeature _feature;

    public RequestTimeoutFeatureCollection(IHttpFeatureCollection inner, RequestTimeoutFeature feature)
    {
        _inner = inner;
        _feature = feature;
    }

    public int Version => _inner.Version;

    public IHttpFeature? Get(string name) => _inner.Get(name);

    public void Set(IHttpFeature? feature)
    {
        _inner.Set(feature);

        if (feature is IRouteMatchFeature match &&
            match.Metadata.GetMetadata<RequestTimeoutMetadata>() is { } metadata)
        {
            _feature.ApplyEndpointPolicy(metadata);
        }
    }

    public bool Remove(string name) => _inner.Remove(name);

    public IEnumerator<IHttpFeature> GetEnumerator() => _inner.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
