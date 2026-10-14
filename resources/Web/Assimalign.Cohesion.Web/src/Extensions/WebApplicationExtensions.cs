using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Internal;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// The composition verbs on the Web pipeline builder: inline middleware, conditional segments that
/// rejoin the pipeline, and terminal middleware.
/// </summary>
/// <remarks>
/// These verbs depend on nothing but the pipeline builder, so they stay in the Web root. The branches
/// that do not rejoin, <c>Map(path)</c> and <c>MapWhen</c>, end in the standard terminal that runs a
/// selected endpoint, and ship in <c>Assimalign.Cohesion.Web.Routing</c> with it.
/// </remarks>
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

        /// <summary>
        /// Runs <paramref name="configure"/>'s middleware for requests that satisfy
        /// <paramref name="predicate"/>, then rejoins the containing pipeline: the segment's last
        /// middleware continues into the middleware registered after this call.
        /// </summary>
        /// <remarks>
        /// The segment's middleware composes with the containing pipeline's application context, so a
        /// middleware that reads the context at composition time composes inside the segment exactly as
        /// it does on the application.
        /// </remarks>
        /// <param name="predicate">Decides, per request, whether the segment runs.</param>
        /// <param name="configure">Configures the segment's middleware.</param>
        /// <returns>The containing pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseWhen(Func<IHttpContext, bool> predicate, Action<IWebApplicationPipelineBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(predicate);
            ArgumentNullException.ThrowIfNull(configure);

            WebApplicationBranchBuilder branch = new();
            configure(branch);

            return builder.Use((IWebApplicationContext application, WebApplicationMiddleware next) =>
            {
                WebApplicationMiddleware segment = branch.Compose(application, next);

                return context => predicate(context) ? segment.Invoke(context) : next.Invoke(context);
            });
        }

        /// <summary>
        /// Adds terminal middleware: <paramref name="terminal"/> handles every request that reaches it, and
        /// nothing registered after it runs.
        /// </summary>
        /// <param name="terminal">The terminal handler.</param>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="terminal"/> is <see langword="null"/>.</exception>
        public void Run(WebApplicationMiddleware terminal)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(terminal);

            builder.Use((WebApplicationMiddleware _) => terminal);
        }
    }
}
