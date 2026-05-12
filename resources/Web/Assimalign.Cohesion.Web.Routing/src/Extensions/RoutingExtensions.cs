using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Adds routing middleware to web application pipelines.
/// </summary>
public static class RoutingExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder, IWebApplication
    {
        /// <summary>
        /// Adds routing to the current web application pipeline.
        /// </summary>
        /// <returns>The shared router builder used to map routes.</returns>
        public IRouterBuilder UseRouting()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.Use(async (context, next) =>
            {
                using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                
                CancellationToken cancellationToken = cancellationTokenSource.Token;

                IRouter router = RouterBuilder.Shared.Build();

                await router.RouteAsync(context, cancellationToken).ConfigureAwait(false);

                if (!context.TryGetRoute(out _))
                {
                    await next.Invoke(context).ConfigureAwait(false);
                }
            });

            return RouterBuilder.Shared;
        }
    }
}
