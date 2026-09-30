using System;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.StaticFiles.Internal;

namespace Assimalign.Cohesion.Web.StaticFiles;

/// <summary>
/// Fallback routes that answer with a file from the application's web root: the single-page
/// application shape, where every client-side route serves <c>index.html</c>.
/// </summary>
public static class StaticFilesFallbackExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IWebApplicationPipelineBuilder, IWebApplication
    {
        /// <summary>
        /// Maps the application's fallback route to a file in the web root, for example
        /// <c>app.MapFallbackToFile("index.html")</c>. It answers <c>GET</c> and <c>HEAD</c> requests whose path no
        /// other route matches and whose last segment names no file, so a missing asset such as
        /// <c>/app.js</c> is still a 404.
        /// </summary>
        /// <remarks>
        /// The file is served by the static-files machinery (content type, validators, conditional GET,
        /// precompressed siblings) with <paramref name="configure"/>'s options. A missing file, or an
        /// application without a web root, answers 404. Register <c>UseStaticFiles()</c> before
        /// <c>UseRouting()</c> so existing assets are served first.
        /// </remarks>
        /// <param name="filePath">The file to serve, relative to the web root (for example <c>index.html</c>).</param>
        /// <param name="configure">Configures the options the file is served with; <see cref="StaticFilesOptions.RequestPath"/> is ignored.</param>
        /// <returns>The fallback route's builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty or not a path inside the web root, or the options are invalid.</exception>
        /// <exception cref="InvalidOperationException">Routing has not been registered (call <c>AddRouting</c>), or the route table has already been built.</exception>
        public IRouterRouteBuilder MapFallbackToFile(string filePath, Action<StaticFilesOptions>? configure = null)
        {
            return builder.MapFallbackToFile("{**path:nonfile}", filePath, configure);
        }

        /// <summary>
        /// Maps a fallback route with its own template (for example <c>admin/{**path:nonfile}</c>) to a file in
        /// the web root, for a single-page application mounted below the site root.
        /// </summary>
        /// <param name="pattern">The fallback route's template.</param>
        /// <param name="filePath">The file to serve, relative to the web root.</param>
        /// <param name="configure">Configures the options the file is served with; <see cref="StaticFilesOptions.RequestPath"/> is ignored.</param>
        /// <returns>The fallback route's builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="pattern"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is empty or not a path inside the web root, or the options are invalid.</exception>
        /// <exception cref="InvalidOperationException">Routing has not been registered (call <c>AddRouting</c>), or the route table has already been built.</exception>
        public IRouterRouteBuilder MapFallbackToFile(string pattern, string filePath, Action<StaticFilesOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(pattern);
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            string file = "/" + filePath.TrimStart('/');
            if (StaticFilePath.HasUnsafeSegments(file))
            {
                throw new ArgumentException($"The fallback file must be a path inside the web root: '{filePath}'.", nameof(filePath));
            }

            StaticFilesOptions options = new();
            configure?.Invoke(options);
            options.RequestPath = HttpPath.Root;
            WebApplicationStaticFilesExtensions.Validate(options);

            IRouterBuilder routes = builder.Context.Features.OfType<IRouterFeature>().FirstOrDefault()?.Builder
                ?? throw new InvalidOperationException(
                    "No router builder was registered. Call AddRouting() on the application builder before mapping a fallback.");

            StaticFilesMiddleware? middleware = WebRootStaticFiles.TryCreate(builder.Context, options);

            return routes.MapFallback(pattern, new RouterRouteHandler(context => middleware is null
                ? NotFoundAsync(context)
                : middleware.ServeAsync(context, file, NotFoundAsync)));
        }
    }

    private static Task NotFoundAsync(IHttpContext context)
    {
        context.Response.StatusCode = HttpStatusCode.NotFound;
        return Task.CompletedTask;
    }
}
