using System;
using System.Linq;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

/// <summary>
/// API-oriented endpoint mapping helpers for web application pipelines.
/// </summary>
/// <remarks>
/// Two families of overloads live here:
/// <list type="bullet">
/// <item>
/// The <see cref="WebApplicationMiddleware"/> overloads register a terminal endpoint verbatim — no
/// parameter binding takes place.
/// </item>
/// <item>
/// The <see cref="Delegate"/> overloads accept a typed handler lambda (for example
/// <c>(int id, IHttpContext context) =&gt; ...</c>). They are placeholders: the Cohesion Web source
/// generator (<c>Assimalign.Cohesion.SourceGeneration.Web</c>) intercepts the call site and
/// substitutes an AOT-safe binding thunk. Their bodies throw, so reaching one at run time signals
/// the generator was not wired in.
/// </item>
/// </list>
/// </remarks>
public static class WebApplicationPipelineBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder, IWebApplication
    {
        /// <summary>
        /// Maps a route to the supplied terminal middleware.
        /// </summary>
        /// <param name="method">The HTTP method the route matches.</param>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="middleware">The middleware to execute when the route matches.</param>
        /// <returns>The current pipeline builder.</returns>
        public IWebApplicationPipelineBuilder Map(HttpMethod method, string pattern, WebApplicationMiddleware middleware)
        {
            ArgumentException.ThrowIfNullOrEmpty(pattern);
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(middleware);
            IWebApplicationContext context = builder.Context;

            IRouterFeature? feature = context.Features.OfType<IRouterFeature>().FirstOrDefault();

            if (feature is null || feature.Builder is null)
            {
                throw new InvalidOperationException("No router builder was registered. Call AddRouting() on the application builder before mapping endpoints.");
            }

            feature.Builder.Map(new Route(method, pattern, new RouterRouteHandler(middleware)));

            return builder;
        }

        /// <summary>
        /// Maps a GET route pattern to the supplied terminal middleware.
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

        /// <summary>
        /// Maps a route to a typed handler whose parameters are bound from the request. The Cohesion
        /// Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="method">The HTTP method the route matches.</param>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The current pipeline builder.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IWebApplicationPipelineBuilder Map(HttpMethod method, string pattern, Delegate handler)
            => throw RequiresSourceGenerator();

        /// <summary>
        /// Maps a GET route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The current pipeline builder.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IWebApplicationPipelineBuilder MapGet(string pattern, Delegate handler)
            => throw RequiresSourceGenerator();

        /// <summary>
        /// Maps a POST route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The current pipeline builder.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IWebApplicationPipelineBuilder MapPost(string pattern, Delegate handler)
            => throw RequiresSourceGenerator();

        /// <summary>
        /// Maps a PUT route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The current pipeline builder.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IWebApplicationPipelineBuilder MapPut(string pattern, Delegate handler)
            => throw RequiresSourceGenerator();

        /// <summary>
        /// Maps a PATCH route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The current pipeline builder.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IWebApplicationPipelineBuilder MapPatch(string pattern, Delegate handler)
            => throw RequiresSourceGenerator();

        /// <summary>
        /// Maps a DELETE route to a typed handler whose parameters are bound from the request. The
        /// Cohesion Web source generator intercepts this call and substitutes an AOT-safe binding thunk.
        /// </summary>
        /// <param name="pattern">The route pattern to parse.</param>
        /// <param name="handler">The typed handler lambda whose parameters are bound from the request.</param>
        /// <returns>The current pipeline builder.</returns>
        /// <exception cref="NotSupportedException">Always thrown when the source generator did not rewrite the call site.</exception>
        public IWebApplicationPipelineBuilder MapDelete(string pattern, Delegate handler)
            => throw RequiresSourceGenerator();
    }

    /// <summary>
    /// Produces the exception thrown by the typed <see cref="Delegate"/> overloads when the source
    /// generator did not intercept the call site.
    /// </summary>
    /// <returns>The exception to throw.</returns>
    private static NotSupportedException RequiresSourceGenerator()
        => new(
            "This typed endpoint overload is a placeholder that the Cohesion Web source generator " +
            "(Assimalign.Cohesion.SourceGeneration.Web) rewrites at the call site. Reaching it at run time means the " +
            "generator did not intercept the call: reference the generator with " +
            "<CohesionAnalyzerReference Include=\"Assimalign.Cohesion.SourceGeneration.Web\" /> and allow-list its " +
            "generated namespace with <InterceptorsNamespaces>$(InterceptorsNamespaces);Assimalign.Cohesion.Web.Api.Generated</InterceptorsNamespaces>.");
}
