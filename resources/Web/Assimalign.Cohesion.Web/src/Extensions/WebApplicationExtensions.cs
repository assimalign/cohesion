using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// The inline-middleware verb on the Web pipeline builder.
/// </summary>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds inline middleware that receives the exchange and the rest of the pipeline.
        /// </summary>
        /// <remarks>
        /// Middleware runs in registration order. It continues the pipeline by invoking the next middleware it
        /// is given, or answers the exchange itself by not invoking it.
        /// </remarks>
        /// <param name="middleware">
        /// The middleware: it receives the exchange and the next middleware, and its task completes when it has
        /// handled the exchange.
        /// </param>
        /// <returns>The same builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="middleware"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder Use(Func<IHttpContext, WebApplicationMiddleware, Task> middleware)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(middleware);

            Func<IHttpContext, WebApplicationMiddleware, Task> middleware2 = middleware;

            builder.Use((WebApplicationMiddleware next) => (IHttpContext context) =>
            {
                return middleware2.Invoke(context, next);
            });

            return builder;
        }
    }
}
