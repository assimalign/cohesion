using System;
using System.Linq;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.HttpsPolicy.Internal;

namespace Assimalign.Cohesion.Web.HttpsPolicy;

/// <summary>
/// Pipeline-builder extension that places HTTP-to-HTTPS redirection in the Web application middleware
/// pipeline.
/// </summary>
public static class HttpsRedirectionExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the middleware that answers an insecure request with a method-preserving redirect to
        /// the same host and request target on the <c>https</c> scheme, and lets an already-secure
        /// request pass through: the middleware the application registered with
        /// <c>builder.Services.AddHttpsRedirection(redirection =&gt; ...)</c>, or one with the defaults
        /// (<c>307</c> to port <c>443</c>) when it registered none.
        /// </summary>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The status and port are configured where the redirection is registered
        /// (<see cref="HttpsRedirectionBuilder"/>); this verb only positions it. When the application
        /// registered the redirection more than once, the last registration wins.
        /// </para>
        /// <para>
        /// Place this <em>early</em>, before response-shaping middleware such as compression or
        /// serialization, so an insecure request is redirected and short-circuited before any work is
        /// done on a response that is about to be discarded. The redirect is bodyless (status +
        /// <c>Location</c>).
        /// </para>
        /// <para>
        /// Connection security is the effective typed scheme
        /// (<see cref="HttpContextForwardedExtensions.EffectiveScheme"/>): the scheme a trusted proxy
        /// asserted when the forwarded-headers middleware resolved one, otherwise the transport-derived
        /// scheme (from the listener's transport security capability, #763). Behind a TLS-terminating
        /// proxy, place <c>UseForwardedHeaders</c> <em>before</em> this verb so the proxy's
        /// <c>https</c> is recognized and requests are not redirected in a loop; the redirect
        /// <c>Location</c> then targets the effective host the client addressed. There is no
        /// scheme-string or header sniffing here.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseHttpsRedirection()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<HttpsRedirectionMiddleware>().LastOrDefault()
                    ?? new HttpsRedirectionBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
