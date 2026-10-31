using System;
using System.Linq;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Compression.Internal;

namespace Assimalign.Cohesion.Web.Compression;

/// <summary>
/// Pipeline-builder verb that places transparent request decompression in a web application. It
/// composes against the root <see cref="IWebApplicationPipelineBuilder"/> and is dependency-free: the
/// middleware is configured where it is registered and resolves nothing per request.
/// </summary>
public static class RequestDecompressionExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the middleware that transparently decompresses gzip/Brotli/deflate request bodies per
        /// the request's <c>Content-Encoding</c> before handlers read them, answering <c>415</c> for an
        /// unsupported coding and <c>413</c> when the decompressed body exceeds the configured guard: the
        /// middleware the application registered with
        /// <c>builder.Services.AddRequestDecompression(decompression =&gt; ...)</c>, or one with the default
        /// 100&#8239;MiB guard when it registered none.
        /// </summary>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The decompressed-size guard is configured where the middleware is registered
        /// (<see cref="RequestDecompressionBuilder"/>); this verb only positions it. When the application
        /// registered decompression more than once, the last registration wins.
        /// </para>
        /// <para>
        /// Place it early &#8212; before routing and any handler that reads the body &#8212; but
        /// <em>after</em> <c>UseErrorHandling</c>, so the exception boundary sits outside it and the
        /// decompressed-size guard is translated into a clean <c>413</c> rather than a generic
        /// <c>500</c>.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseRequestDecompression()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<RequestDecompressionMiddleware>().LastOrDefault()
                    ?? new RequestDecompressionBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
