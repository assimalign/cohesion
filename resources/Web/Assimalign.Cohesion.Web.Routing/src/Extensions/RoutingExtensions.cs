using System;
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
/// </remarks>
public static class RoutingExtensions
{
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
        /// Map routes before the application starts. The middleware builds the application's router
        /// once, when the request pipeline is built at startup, so an invalid route table (for example
        /// a duplicate route name) fails the start instead of a request, and mapping a route after
        /// that throws <see cref="InvalidOperationException"/>. A matched route's handler receives the
        /// request's <see cref="IHttpContext.RequestCancelled"/> token.
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

                return context => DispatchAsync(router, context, next);
            });

            return feature.Builder;
        }
    }

    private static Task DispatchAsync(IRouter router, IHttpContext context, WebApplicationMiddleware next)
    {
        RouteMatch match = router.Match(context);

        switch (match.Status)
        {
            case RouteMatchStatus.Matched:
                context.SetRouteMatch(match.Route!, match.Values);
                return match.Route!.Handler.InvokeAsync(context, context.RequestCancelled);

            case RouteMatchStatus.MethodNotAllowed:
                // A route matched the path but not the method: emit 405 with an Allow header and
                // short-circuit rather than falling through to the terminal 404 pipeline.
                Router.ApplyMethodNotAllowed(context, match);
                return Task.CompletedTask;

            default:
                return next.Invoke(context);
        }
    }
}
