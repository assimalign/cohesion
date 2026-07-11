using System;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.StaticFiles.Internal;

namespace Assimalign.Cohesion.Web.StaticFiles;

/// <summary>
/// Pipeline-builder members that serve static files from a mounted <see cref="IFileSystem"/>.
/// </summary>
/// <remarks>
/// Registration is dependency-free per the Web-area rules: the file system and options are
/// supplied explicitly at composition time and snapshotted into the middleware — no service
/// container, no configuration binding, no request-time service location. Mount any
/// <see cref="IFileSystem"/> implementation (physical, in-memory, aggregate); the middleware
/// can never address anything outside that mount.
/// </remarks>
public static class WebApplicationStaticFilesExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Serves <c>GET</c>/<c>HEAD</c> requests from <paramref name="fileSystem"/> at the site
        /// root with default options: default documents (<c>index.html</c>/<c>index.htm</c>),
        /// conditional GET, single byte-range support, precompressed <c>.br</c>/<c>.gz</c>
        /// sibling negotiation, and unknown extensions blocked.
        /// </summary>
        /// <param name="fileSystem">The file system to serve as the content root.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="fileSystem"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseStaticFiles(IFileSystem fileSystem)
            => builder.UseStaticFiles(fileSystem, configure: null);

        /// <summary>
        /// Serves <c>GET</c>/<c>HEAD</c> requests from <paramref name="fileSystem"/> with
        /// caller-configured <see cref="StaticFilesOptions"/> (request-path prefix, default
        /// documents, <c>Cache-Control</c>, content-type overlays, precompression).
        /// </summary>
        /// <param name="fileSystem">The file system to serve as the content root.</param>
        /// <param name="configure">A callback that configures the composition-time options.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="fileSystem"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown when the configured options are invalid: a request path that does not begin
        /// with <c>/</c>, a default-document name that is empty or contains a path separator or
        /// dot segment, a <c>Cache-Control</c> value that does not parse per RFC 9111, or an
        /// empty fallback content type while <see cref="StaticFilesOptions.ServeUnknownContentTypes"/> is enabled.
        /// </exception>
        public IWebApplicationPipelineBuilder UseStaticFiles(IFileSystem fileSystem, Action<StaticFilesOptions>? configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(fileSystem);

            var options = new StaticFilesOptions();
            configure?.Invoke(options);
            Validate(options);

            return builder.Use(new StaticFilesMiddleware(fileSystem, options));
        }
    }

    private static void Validate(StaticFilesOptions options)
    {
        // HttpPath also admits the "*" asterisk form; a mount prefix must be an origin-form path.
        if (options.RequestPath.Value.Length == 0 || options.RequestPath.Value[0] != '/')
        {
            throw new ArgumentException(
                $"The static-files request path must begin with '/': '{options.RequestPath.Value}'.");
        }

        foreach (string document in options.DefaultDocuments)
        {
            if (string.IsNullOrWhiteSpace(document)
                || document.AsSpan().ContainsAny('/', '\\')
                || document.AsSpan().IndexOfAnyExcept('.') < 0)
            {
                throw new ArgumentException(
                    $"Default-document names must be bare file names: '{document}'.");
            }
        }

        if (!string.IsNullOrEmpty(options.CacheControl)
            && !HttpCacheControl.TryParse(options.CacheControl, out _))
        {
            throw new ArgumentException(
                $"The Cache-Control value is not a valid RFC 9111 field value: '{options.CacheControl}'.");
        }

        if (options.ServeUnknownContentTypes && string.IsNullOrWhiteSpace(options.FallbackContentType))
        {
            throw new ArgumentException(
                "A fallback content type is required when unknown content types are served.");
        }
    }
}
