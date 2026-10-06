using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Metadata;

namespace Assimalign.Cohesion.Web.Routing.Internal;

/// <summary>
/// Default <see cref="IRouteMatchFeature"/> implementation installed on the HTTP context feature
/// collection when a request matches a route. It is also the exchange's
/// <see cref="IWebEndpointFeature"/>: the pipeline's terminal runs the matched route's handler through
/// <see cref="Endpoint"/>.
/// </summary>
/// <remarks>
/// Every endpoint publication shares the <see cref="IWebEndpointFeature"/> slot name, so a later
/// publication (a re-route, or the 405 endpoint) replaces an earlier one instead of leaving two
/// endpoints on the exchange.
/// </remarks>
internal sealed class RouteMatchFeature : IRouteMatchFeature, IWebEndpointFeature
{
    private readonly WebApplicationMiddleware _endpoint;
    private HashSet<string>? _acknowledged;

    /// <summary>
    /// Initializes a new route-match feature whose endpoint runs the matched route's handler.
    /// </summary>
    /// <param name="route">The matched route.</param>
    /// <param name="values">The captured route values.</param>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> or <paramref name="values"/> is <see langword="null"/>.</exception>
    public RouteMatchFeature(IRouterRoute route, RouteValueDictionary values)
    {
        Route = route ?? throw new ArgumentNullException(nameof(route));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        _endpoint = InvokeRouteAsync;
    }

    /// <summary>
    /// Initializes a new route-match feature for a CORS preflight: <paramref name="candidate"/> is the
    /// route the actual request would reach, and <paramref name="endpoint"/> is what the terminal runs
    /// when no middleware answers the preflight.
    /// </summary>
    /// <param name="candidate">The candidate route for the requested method.</param>
    /// <param name="values">The route values captured for the candidate.</param>
    /// <param name="endpoint">The endpoint that answers an unhandled preflight.</param>
    public RouteMatchFeature(IRouterRoute candidate, RouteValueDictionary values, WebApplicationMiddleware endpoint)
    {
        Route = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        IsPreflight = true;
    }

    /// <inheritdoc />
    public string Name => nameof(IWebEndpointFeature);

    /// <inheritdoc />
    public IRouterRoute? Route { get; }

    /// <inheritdoc />
    public RouteValueDictionary? Values { get; }

    /// <inheritdoc />
    public IRouterRouteMetadataCollection Metadata => Route?.Metadata ?? RouterRouteMetadataCollection.Empty;

    /// <inheritdoc />
    public bool IsPreflight { get; }

    /// <inheritdoc />
    public WebApplicationMiddleware Endpoint => _endpoint;

    /// <summary>
    /// Gets the matched route's template as telemetry reports it (<c>http.route</c>), with a leading
    /// <c>/</c>; for a CORS preflight, the candidate's. <see langword="null"/> for a route without a
    /// pattern.
    /// </summary>
    public string? RouteTemplate => Route?.Pattern?.TelemetryTemplate;

    /// <summary>
    /// Records that <paramref name="middleware"/> processed this endpoint for the current request.
    /// </summary>
    /// <param name="middleware">The pipeline verb of the middleware, for example <c>UseRateLimiting</c>.</param>
    public void Acknowledge(string middleware)
    {
        (_acknowledged ??= new HashSet<string>(StringComparer.Ordinal)).Add(middleware);
    }

    private Task InvokeRouteAsync(IHttpContext context)
    {
        EnsureRequiredMiddlewareRan();

        return Route!.Handler.InvokeAsync(context, context.RequestCancelled);
    }

    // Fails closed when endpoint metadata names a middleware that never processed the request, so a
    // declared policy never silently goes unenforced (a missing middleware, or one registered ahead of
    // UseRouting where no endpoint was known yet).
    private void EnsureRequiredMiddlewareRan()
    {
        IRouterRouteMetadataCollection metadata = Route!.Metadata;

        for (int i = 0; i < metadata.Count; i++)
        {
            if (metadata[i] is IRouteMiddlewareMetadata { RequiredMiddleware: { Length: > 0 } middleware }
                && _acknowledged?.Contains(middleware) != true
                && !IsSuperseded(metadata, i))
            {
                throw new InvalidOperationException(
                    $"The endpoint '{Describe(Route)}' declares {metadata[i].GetType().Name}, which only " +
                    $"{middleware}() applies, but {middleware}() did not process this request. Register " +
                    $"{middleware}() after UseRouting() so it runs before the endpoint.");
            }
        }
    }

    // Consumers read metadata last-wins, so an item that a later item of the same type replaces (a
    // group's policy that the route disables, for example) is never applied and requires nothing.
    // Metadata lists are short; the scan allocates nothing.
    private static bool IsSuperseded(IRouterRouteMetadataCollection metadata, int index)
    {
        Type type = metadata[index].GetType();

        for (int j = index + 1; j < metadata.Count; j++)
        {
            if (metadata[j].GetType() == type)
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(IRouterRoute route)
    {
        string methods = route.Methods.Count == 0 ? "*" : string.Join(",", route.Methods);
        return route.Pattern is { } pattern ? $"{methods} {pattern.RawText}" : methods;
    }
}
