using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Metadata;

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
/// <c>UseRouting</c> installs the feature when a request matches a route (see
/// the <c>SetRouteMatch</c> extension on <see cref="IHttpContext"/>) and then
/// calls <c>next</c>: every middleware registered after <c>UseRouting</c> runs
/// with the match known, and the pipeline's terminal runs the matched route's
/// handler. When no route has matched, including a 405 (the path matched but
/// the method did not), no feature is present and
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
    /// Gets whether the request is a CORS preflight and <see cref="Route"/> is only its candidate
    /// endpoint.
    /// </summary>
    /// <remarks>
    /// A CORS preflight is an <c>OPTIONS</c> request carrying <c>Origin</c> and
    /// <c>Access-Control-Request-Method</c> (Fetch Standard). When no route accepts <c>OPTIONS</c> on
    /// the path, routing resolves the route the actual request would reach with the requested method
    /// and publishes it with this flag set, so CORS can read the candidate's metadata. The candidate
    /// never runs for the preflight: when no middleware answers the preflight, the terminal answers
    /// it as the plain <c>OPTIONS</c> request it is (<c>405</c> with <c>Allow</c>). Middleware that apply
    /// endpoint policies to the actual request (authorization, rate limits) skip a preflight, which by
    /// definition carries no credentials and runs no handler.
    /// </remarks>
    bool IsPreflight => false;

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
