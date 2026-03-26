using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

/// <summary>
/// API-oriented endpoint mapping helpers for web application pipelines.
/// </summary>
public static class WebApplicationPipelineBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder, IWebApplication
    {
        /// <summary>
        /// Maps a GET route to the supplied middleware.
        /// </summary>
        /// <param name="route">The route definition to evaluate.</param>
        /// <param name="middleware">The middleware to execute when the route matches.</param>
        /// <returns>The current pipeline builder.</returns>
        public IWebApplicationPipelineBuilder Map(HttpMethod method, string pattern, WebApplicationMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNullOrEmpty(pattern);
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(middleware);
            IWebApplicationContext context = builder.Context;

            IServiceProvider? serviceProvider = context.ServiceProvider;

            if (serviceProvider is null)
            {
                throw new InvalidOperationException("No Service Provider was registered");
            }

            IRouterBuilder? routerBuilder = serviceProvider.GetService(typeof(IRouterBuilder)) as IRouterBuilder;

            if (routerBuilder is null)
            {
                throw new InvalidOperationException("No router builder was provided");
            }

            routerBuilder.Map(new Route(method, pattern, new RouterRouteHandler(middleware)));

            return builder;



            //builder.Use(async (context, next) =>
            //{
            //    if (!route.TryMatch(context, out IRouterRoute? matchedRoute, out RouteValueDictionary values) ||
            //        matchedRoute is null)
            //    {
            //        await next(context).ConfigureAwait(false);
            //        return;
            //    }

            //    context.SetRouteMatch(matchedRoute, values);
            //    await middleware(context, next).ConfigureAwait(false);
            //});
        }

        /// <summary>
        /// Maps a GET route pattern to the supplied middleware.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="middleware">The middleware to execute when the route matches.</param>
        /// <returns>The current pipeline builder.</returns>
        public IWebApplicationPipelineBuilder MapGet(
            string pattern,
            WebApplicationMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrEmpty(pattern);
            ArgumentNullException.ThrowIfNull(middleware);

            return builder.Map(HttpMethod.Get, pattern, middleware);
        }
    }
}
