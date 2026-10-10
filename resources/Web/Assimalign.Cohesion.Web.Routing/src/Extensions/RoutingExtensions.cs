using System;
using System.Buffers;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Internal;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Adds routing to web applications and wires the routing middleware into their pipelines.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddRouting</c> (builder time) registers the per-application <see cref="IRouterFeature"/>, and
/// <c>UseRouting</c> (pipeline time) resolves that <em>same</em> feature and returns its
/// <see cref="IRouterFeature.Builder"/>. Both therefore operate on one per-application builder — there
/// is no process-wide shared builder, so route tables never leak between applications hosted in the
/// same process (issue #789).
/// </para>
/// <para>
/// The router is built once, when the application's request pipeline is built at startup: the
/// pipeline builder invokes <c>UseRouting</c>'s middleware factory as it composes the pipeline, and
/// the factory builds the router there. Route-table errors therefore fail startup, and a route mapped
/// after that throws instead of being silently ignored.
/// </para>
/// <para>
/// <c>UseRouting</c> selects the endpoint; it does not run it (#1054). It publishes the match and calls
/// <c>next</c>, so every middleware registered after it runs with the endpoint and its metadata known,
/// and the pipeline's terminal (<see cref="WebApplicationTerminal"/>) runs the endpoint through
/// <see cref="IWebEndpointFeature"/>.
/// Dispatch is implicit: there is no separate <c>UseEndpoints</c> step to register.
/// </para>
/// </remarks>
public static class RoutingExtensions
{
    // RFC 9110 §5.6.2 token characters, the set HttpMethod accepts, and the longest method it accepts.
    private static readonly SearchValues<char> _methodTokenCharacters =
        SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");
    private const int maximumMethodLength = 32;

    extension(IWebApplicationBuilder builder)
    {
        /// <summary>
        /// Registers routing for the web application by installing the per-application
        /// <see cref="IRouterFeature"/> on the HTTP context feature collection.
        /// </summary>
        /// <returns>The web application builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationBuilder AddRouting()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.AddFeature(new RouterFeature());
        }
    }

    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder, IWebApplication
    {
        /// <summary>
        /// Adds the routing middleware to the web application pipeline and returns the
        /// per-application router builder to map routes into.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The middleware matches the request and publishes the result, then always calls <c>next</c>:
        /// </para>
        /// <list type="bullet">
        /// <item>A match publishes the route (an <see cref="IRouteMatchFeature"/>, read through
        /// <c>context.GetEndpointMetadata&lt;T&gt;()</c>) as the exchange's endpoint. The pipeline's
        /// terminal runs its handler, with the request's <see cref="IHttpContext.RequestCancelled"/>
        /// token, after every middleware registered behind <c>UseRouting</c> has run.</item>
        /// <item>A path match with the wrong method publishes a 405 endpoint: the terminal answers
        /// <c>405 Method Not Allowed</c> with an <c>Allow</c> header. <c>HEAD</c> is served by a
        /// <c>GET</c> route.</item>
        /// <item>A CORS preflight to a path with no <c>OPTIONS</c> route publishes the candidate route
        /// for the requested method with <see cref="IRouteMatchFeature.IsPreflight"/> set; the
        /// candidate never runs for the preflight.</item>
        /// <item>No match publishes nothing, and the request reaches the terminal's 404.</item>
        /// </list>
        /// <para>
        /// Register middleware that reads endpoint metadata (rate limiting, request timeouts, output
        /// caching, CORS, authorization) after <c>UseRouting</c>. Middleware registered after
        /// <c>UseRouting</c> now runs for matched requests too; before #1054 routing was terminal for
        /// them.
        /// </para>
        /// <para>
        /// Map routes before the application starts. The middleware builds the application's router
        /// once, when the request pipeline is built at startup, so an invalid route table (for example
        /// a duplicate route name) fails the start instead of a request, and mapping a route after
        /// that throws <see cref="InvalidOperationException"/>.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The application's <see cref="IRouterFeature.Builder"/> — the same builder that
        /// <c>AddRouting</c> registered and that <c>MapGet</c>/<c>Map</c> map into.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// Routing has not been registered on the application (call <c>AddRouting</c> before <c>UseRouting</c>).
        /// </exception>
        public IRouterBuilder UseRouting()
        {
            ArgumentNullException.ThrowIfNull(builder);

            IRouterFeature feature = builder.Context.Features.OfType<IRouterFeature>().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Routing has not been registered. Call AddRouting() on the web application builder before UseRouting().");

            builder.Use((WebApplicationMiddleware next) =>
            {
                // The pipeline builder invokes this factory when it composes the pipeline, which the
                // host does at startup. Building the router here fixes the route table before the
                // first request: an invalid table fails startup, and a later Map throws.
                IRouter router = feature.Router;

                return context =>
                {
                    SelectEndpoint(router, context);
                    return next.Invoke(context);
                };
            });

            return feature.Builder;
        }
    }

    private static void SelectEndpoint(IRouter router, IHttpContext context)
    {
        RouteMatch match = router.Match(context);

        switch (match.Status)
        {
            case RouteMatchStatus.Matched:
                context.SetRouteMatch(match.Route!, match.Values);
                break;

            case RouteMatchStatus.MethodNotAllowed:
                // The path matched but not the method. A CORS preflight (an OPTIONS request naming the
                // actual request's method) to a path with no OPTIONS route resolves the candidate
                // endpoint for that method so CORS can read its metadata; the candidate never runs for
                // the preflight. Anything else is a 405, which the terminal answers.
                if (TryGetPreflightMethod(context.Request, out HttpMethod requestedMethod)
                    && router.Match(context, requestedMethod) is { Status: RouteMatchStatus.Matched } candidate)
                {
                    WebApplicationMiddleware unhandledPreflight = new MethodNotAllowedEndpointFeature(match).Endpoint;
                    context.Features.Set<IRouteMatchFeature>(new RouteMatchFeature(candidate.Route!, candidate.Values, unhandledPreflight));
                    break;
                }

                context.Features.Set<IWebEndpointFeature>(new MethodNotAllowedEndpointFeature(match));
                break;

            default:
                // Nothing matched: clear any endpoint an earlier selection published, so the request
                // reaches the terminal's 404 instead of a stale endpoint.
                context.Features.Set<IWebEndpointFeature>(null);
                break;
        }
    }

    // A CORS-preflight request (Fetch Standard §3.2.2): OPTIONS with an Origin and a single, well-formed
    // Access-Control-Request-Method. A malformed method token is not a preflight.
    private static bool TryGetPreflightMethod(IHttpRequest request, out HttpMethod method)
    {
        method = default;

        if (request.Method != HttpMethod.Options
            || !request.Headers.ContainsKey(HttpHeaderKey.Origin)
            || !request.Headers.TryGetValue(HttpHeaderKey.AccessControlRequestMethod, out HttpHeaderValue requested)
            || requested.Count != 1)
        {
            return false;
        }

        string token = requested.Value.Trim();

        if (token.Length == 0 || token.Length > maximumMethodLength || token.AsSpan().ContainsAnyExcept(_methodTokenCharacters))
        {
            return false;
        }

        method = HttpMethod.GetCanonicalizedValue(token);
        return true;
    }
}
