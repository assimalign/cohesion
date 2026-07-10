using System;

namespace Assimalign.Cohesion.Web.Routing.Metadata;

/// <summary>
/// Endpoint metadata that names a route for outbound URL generation. Attach an instance to a
/// route's <see cref="IRouterRouteMetadataCollection"/> to make the route addressable by name
/// through an <see cref="ILinkGenerator"/>.
/// </summary>
/// <remarks>
/// <para>
/// The link generator resolves this metadata with last-wins semantics
/// (<see cref="IRouterRouteMetadataCollection.GetMetadata{TMetadata}"/>), so an endpoint-level
/// name overrides a broader (e.g. group-level) one. Route names are compared case-insensitively
/// and must be unique across the routes of a router: a duplicate fails when the route table is
/// built (at <see cref="IRouterBuilder.Build"/> / <see cref="Router"/> construction), not at
/// request time.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract — there is deliberately no
/// <c>IRouteNameMetadata</c> interface. Metadata items in the bag are immutable data carriers,
/// and the sealed type guarantees the validated, immutable name the link generator indexes at
/// construction.
/// </para>
/// </remarks>
public sealed class RouteNameMetadata
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

    /// <summary>
    /// Gets the name of the route. Never <see langword="null"/> or empty.
    /// </summary>
    public string RouteName { get; }
}
