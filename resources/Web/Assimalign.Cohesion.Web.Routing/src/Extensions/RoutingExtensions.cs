using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

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

            builder.Use(async (context, next) =>
            {
                using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(context.RequestCancelled);

                CancellationToken cancellationToken = cancellationTokenSource.Token;

                IHttpFeatureCollection? features = context.Features;

                if (features is null)
                {
                    throw new InvalidOperationException("No HTTP Features are available.");
                }

                IRouterFeature requestFeature = features.Get<IRouterFeature>()
                    ?? throw new InvalidOperationException("No router feature was registered.");
                IRouter router = requestFeature.Router;

                RouteMatch match = router.Match(context);

                switch (match.Status)
                {
                    case RouteMatchStatus.Matched:
                        context.SetRouteMatch(match.Route!, match.Values);
                        await match.Route!.Handler.InvokeAsync(context, cancellationToken).ConfigureAwait(false);
                        break;

                    case RouteMatchStatus.MethodNotAllowed:
                        // A route matched the path but not the method: emit 405 with an Allow header and
                        // short-circuit rather than falling through to the terminal 404 pipeline.
                        context.Response.StatusCode = HttpStatusCode.MethodNotAllowed;
                        context.Response.Headers[HttpHeaderKey.Allow] = match.ToAllowHeaderValue();
                        break;

                    default:
                        await next.Invoke(context).ConfigureAwait(false);
                        break;
                }
            });

            return feature.Builder;
        }
    }
}
