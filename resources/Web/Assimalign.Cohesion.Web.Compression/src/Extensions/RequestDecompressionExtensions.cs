using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Compression.Internal;

namespace Assimalign.Cohesion.Web.Compression;

/// <summary>
/// Pipeline-builder verbs that add transparent request decompression to a web application. They
/// compose against the root <see cref="IWebApplicationPipelineBuilder"/> and are dependency-free:
/// options are captured at builder time and the middleware resolves nothing per request.
/// </summary>
public static class RequestDecompressionExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds middleware that transparently decompresses gzip/Brotli/deflate request bodies per the
        /// request's <c>Content-Encoding</c> before handlers read them, answering <c>415</c> for an
        /// unsupported coding and <c>413</c> when the decompressed body exceeds the configured guard.
        /// </summary>
        /// <param name="configure">
        /// An optional callback to configure the decompressed-size guard. When <see langword="null"/>,
        /// the default 100&#8239;MiB limit applies.
        /// </param>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// Register it early &#8212; before routing and any handler that reads the body &#8212; but
        /// <em>after</em> <c>UseErrorHandling</c>, so the exception boundary sits outside it and the
        /// decompressed-size guard is translated into a clean <c>413</c> rather than a generic
        /// <c>500</c>.
        /// </remarks>
        public IWebApplicationPipelineBuilder UseRequestDecompression(Action<RequestDecompressionOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            RequestDecompressionOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new RequestDecompressionMiddleware(options));
        }
    }
}
