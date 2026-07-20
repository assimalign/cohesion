using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Compression.Internal;

namespace Assimalign.Cohesion.Web.Compression;

/// <summary>
/// Pipeline-builder verbs that add response compression to a web application. They compose against
/// the root <see cref="IWebApplicationPipelineBuilder"/> and are dependency-free: options are
/// captured at builder time and the middleware resolves nothing per request.
/// </summary>
public static class ResponseCompressionExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds middleware that negotiates gzip/Brotli from each request's <c>Accept-Encoding</c> and
        /// compresses eligible responses (configured media types, above the size threshold), stamping
        /// <c>Content-Encoding</c> and <c>Vary: Accept-Encoding</c> and dropping the stale
        /// <c>Content-Length</c>. Already-encoded responses and streamed responses are left untouched,
        /// and HTTPS dynamic content is compressed only when explicitly enabled (BREACH).
        /// </summary>
        /// <param name="configure">
        /// An optional callback to configure the codings, eligible media types, size threshold,
        /// compression level, and the HTTPS opt-in. When <see langword="null"/>, the defaults apply
        /// (gzip and Brotli, the seeded text/structured-text media types, a 1&#8239;KiB threshold,
        /// <c>Fastest</c> level, HTTPS off).
        /// </param>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Both <c>EnableGzip</c> and <c>EnableBrotli</c> were disabled, leaving no coding to offer.</exception>
        /// <remarks>
        /// Register this early in the pipeline so it wraps the response body of every middleware whose
        /// output it should compress. It composes with the exception boundary and content-negotiated
        /// writes: it appends to any existing <c>Vary</c> rather than clobbering it.
        /// </remarks>
        public IWebApplicationPipelineBuilder UseResponseCompression(Action<ResponseCompressionOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            ResponseCompressionOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new ResponseCompressionMiddleware(options));
        }
    }
}
