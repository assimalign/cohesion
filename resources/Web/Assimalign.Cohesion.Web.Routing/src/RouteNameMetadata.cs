using System;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Default <see cref="IRouteNameMetadata"/> implementation that names a route for outbound URL
/// generation.
/// </summary>
/// <remarks>
/// Producers (route mapping, route groups, source generators) construct this type directly and add
/// it to the route's endpoint metadata — the same public-concrete-companion pattern as
/// <see cref="RouterRouteMetadataCollection"/>, applied because metadata items must be
/// constructible from other assemblies.
/// </remarks>
public sealed class RouteNameMetadata : IRouteNameMetadata
{
    /// <summary>
    /// Creates route-name metadata with the supplied name.
    /// </summary>
    /// <param name="routeName">The route name. Compared case-insensitively; must be unique per router.</param>
    /// <exception cref="ArgumentNullException"><paramref name="routeName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="routeName"/> is empty.</exception>
    public RouteNameMetadata(string routeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(routeName);

        RouteName = routeName;
    }

    /// <inheritdoc />
    public string RouteName { get; }
}
