using Assimalign.Cohesion.Http;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Adds routing middleware to web application pipelines.
/// </summary>
public static class RoutingExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
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
                using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                
                CancellationToken cancellationToken = cancellationTokenSource.Token;

                IHttpFeatureCollection? features = context.Features;

                if (features is null)
                {
                    throw new InvalidOperationException("No HTTP Features are available.");
                }

                IRouterFeature feature = features.Get<IRouterFeature>() ?? throw new InvalidOperationException("No router feature was registered.");
                IRouter router = feature.Router;

                await router.RouteAsync(context, cancellationToken).ConfigureAwait(false);

                if (!context.TryGetRoute(out _))
                {
                    await next.Invoke(context).ConfigureAwait(false);
                }
            });

            return RouterBuilder.Shared;
        }
    }


    extension(IWebApplicationBuilder builder)
    {
        public IWebApplicationBuilder AddRouting()
        {
            return builder.AddFeature(new RouterFeature());
        }
    }

    class RouterFeature : IRouterFeature
    {
        private IRouter? _router;

        public IRouterBuilder Builder { get; } = new RouterBuilder();

        public string Name => nameof(IRouterFeature);

        public IRouter Router
        {
            get
            {
                if  (_router is null)
                {
                    _router = Builder.Build();
                }
                return _router;
            }
        }
    }

}
