using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Routing.Metadata;

namespace Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

/// <summary>
/// A stand-in for the endpoint <c>UseRouting</c> publishes: installing it on the feature collection ahead
/// of the output-cache middleware is what routing does before calling <c>next</c>, and installing it
/// downstream of the middleware models routing registered after it.
/// </summary>
internal sealed class FakeRouteMatchFeature : IRouteMatchFeature
{
    private readonly IRouterRouteMetadataCollection _metadata;

    public FakeRouteMatchFeature(params object[] metadata)
        : this(values: null, metadata)
    {
    }

    public FakeRouteMatchFeature(RouteValueDictionary? values, params object[] metadata)
    {
        Values = values;
        _metadata = new RouterRouteMetadataCollection(metadata);
    }

    public string Name => nameof(IRouteMatchFeature);

    public IRouterRoute? Route => null;

    public RouteValueDictionary? Values { get; }

    public IRouterRouteMetadataCollection Metadata => _metadata;
}
