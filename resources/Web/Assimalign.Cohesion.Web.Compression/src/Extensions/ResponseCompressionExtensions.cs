using System;
using System.Linq;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Compression.Internal;

namespace Assimalign.Cohesion.Web.Compression;

/// <summary>
/// Pipeline-builder verb that places response compression in a web application. It composes against
/// the root <see cref="IWebApplicationPipelineBuilder"/> and is dependency-free: the middleware is
/// configured where it is registered and resolves nothing per request.
/// </summary>
public static class ResponseCompressionExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the middleware that negotiates gzip/Brotli from each request's <c>Accept-Encoding</c>
        /// and compresses eligible responses (configured media types, above the size threshold),
        /// stamping <c>Content-Encoding</c> and <c>Vary: Accept-Encoding</c> and dropping the stale
        /// <c>Content-Length</c>: the middleware the application registered with
        /// <c>builder.Services.AddResponseCompression(compression =&gt; ...)</c>, or one with the defaults
        /// when it registered none. Already-encoded responses and streamed responses are left untouched,
        /// and HTTPS dynamic content is compressed only when explicitly enabled (BREACH).
        /// </summary>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The codings, eligible media types, size threshold, compression level, and the HTTPS opt-in
        /// are configured where the middleware is registered (<see cref="ResponseCompressionBuilder"/>);
        /// this verb only positions it. The defaults are gzip and Brotli, the seeded text and
        /// structured-text media types, a 1&#8239;KiB threshold, the <c>Fastest</c> level, and HTTPS off.
        /// When the application registered compression more than once, the last registration wins.
        /// </para>
        /// <para>
        /// Place this early in the pipeline so it wraps the response body of every middleware whose
        /// output it should compress. It composes with the exception boundary and content-negotiated
        /// writes: it appends to any existing <c>Vary</c> rather than clobbering it. With output caching,
        /// place it after <c>UseOutputCache</c>, which itself follows <c>UseRouting</c>, so the cache
        /// stores the encoded variant under the <c>Vary</c> this middleware stamps.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseResponseCompression()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<ResponseCompressionMiddleware>().LastOrDefault()
                    ?? new ResponseCompressionBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
