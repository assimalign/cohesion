using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// The per-exchange <see cref="IHttpFeature"/> that carries the result of route
/// matching &#8211; the matched route (endpoint), its captured route values, and
/// the endpoint's metadata.
/// </summary>
/// <remarks>
/// <para>
/// This feature replaces the previous approach of stashing the matched route and
/// route values under magic-string keys in <see cref="IHttpContext.Items"/>.
/// Route match state is a well-defined contract with a specific shape, so it
/// belongs in the strongly-typed <see cref="IHttpContext.Features"/> collection
/// where authorization, diagnostics, results and tooling can resolve it by
/// contract type rather than by string key.
/// </para>
/// <para>
/// The feature is installed by the router when a request matches a route (see
/// the <c>SetRouteMatch</c> extension on <see cref="IHttpContext"/>). When no
/// route has matched, no feature is present and
/// <see cref="HttpFeatureCollectionExtensions.Get{TFeature}"/> returns
/// <see langword="null"/>.
/// </para>
/// <para>
/// In this routing model the matched route <em>is</em> the endpoint, so
/// <see cref="Metadata"/> surfaces <see cref="IRouterRoute.Metadata"/> directly
/// as the endpoint-metadata seam consumers read without reflection.
/// </para>
/// </remarks>
public interface IRouteMatchFeature : IHttpFeature
{
    /// <summary>
    /// Gets the route that matched the current request, or <see langword="null"/>
    /// when the feature carries no match.
    /// </summary>
    IRouterRoute? Route { get; }

    /// <summary>
    /// Gets the route values captured while matching the current request, or
    /// <see langword="null"/> when the feature carries no match.
    /// </summary>
    RouteValueDictionary? Values { get; }

    /// <summary>
    /// Gets the endpoint metadata of the matched route. Returns
    /// <see cref="RouterRouteMetadataCollection.Empty"/> when no route has matched.
    /// Never <see langword="null"/>.
    /// </summary>
    IRouterRouteMetadataCollection Metadata { get; }
}
