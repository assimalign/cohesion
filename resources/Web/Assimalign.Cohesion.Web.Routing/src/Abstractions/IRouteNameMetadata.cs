namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Endpoint metadata that assigns a name to a route so outbound URLs can be generated for it
/// by name through an <see cref="ILinkGenerator"/>.
/// </summary>
/// <remarks>
/// <para>
/// A route is named by adding an item implementing this contract (typically
/// <see cref="RouteNameMetadata"/>) to its endpoint metadata. When a route carries more than one
/// name item, the last-registered one is effective
/// (<see cref="IRouterRouteMetadataCollection.GetMetadata{TMetadata}"/> is last-wins), which lets a
/// narrower scope override a broader one.
/// </para>
/// <para>
/// Route names are compared case-insensitively and must be unique across the routes of a router.
/// A duplicate name fails when the route table is built (at <see cref="IRouterBuilder.Build"/> /
/// <see cref="Router"/> construction), not at request time.
/// </para>
/// </remarks>
public interface IRouteNameMetadata
{
    /// <summary>
    /// Gets the name of the route. Never <see langword="null"/> or empty.
    /// </summary>
    string RouteName { get; }
}
