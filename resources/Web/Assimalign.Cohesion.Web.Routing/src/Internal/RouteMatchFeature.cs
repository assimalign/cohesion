using System;

using Assimalign.Cohesion.Web.Routing.Metadata;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Default <see cref="IRouteMatchFeature"/> implementation installed on the HTTP
/// context feature collection when a request matches a route.
/// </summary>
internal sealed class RouteMatchFeature : IRouteMatchFeature
{
    /// <summary>
    /// Initializes a new route-match feature for the supplied match.
    /// </summary>
    /// <param name="route">The matched route.</param>
    /// <param name="values">The captured route values.</param>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> or <paramref name="values"/> is <see langword="null"/>.</exception>
    public RouteMatchFeature(IRouterRoute route, RouteValueDictionary values)
    {
        Route = route ?? throw new ArgumentNullException(nameof(route));
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }

    /// <inheritdoc />
    public string Name => nameof(IRouteMatchFeature);

    /// <inheritdoc />
    public IRouterRoute? Route { get; }

    /// <inheritdoc />
    public RouteValueDictionary? Values { get; }

    /// <inheritdoc />
    public IRouterRouteMetadataCollection Metadata => Route?.Metadata ?? RouterRouteMetadataCollection.Empty;
}
