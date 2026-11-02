using System;
using System.Linq;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ForwardedHeaders.Internal;

namespace Assimalign.Cohesion.Web.ForwardedHeaders;

/// <summary>
/// Pipeline-builder extension that places forwarded-headers resolution in the Web application
/// middleware pipeline.
/// </summary>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the forwarded-headers middleware the application registered with
        /// <c>builder.Services.AddForwardedHeaders(forwarded =&gt; ...)</c>. It resolves the effective
        /// client address, scheme, and host from the RFC 7239 <c>Forwarded</c> and/or
        /// <c>X-Forwarded-*</c> headers under the registered trust model, and attaches the result to the
        /// exchange as an <see cref="IHttpForwardedFeature"/>. Raw request headers and the wire-level
        /// scheme/host/connection surfaces are never mutated: downstream code reads the resolved values
        /// through the feature or the <c>context.Effective*</c> members.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The trust model is configured where it is registered (<see cref="ForwardedHeadersBuilder"/>);
        /// this verb only positions the middleware. It has no default trust model, so composing the
        /// pipeline fails when the application registered none. When it registered more than one, the
        /// last registration wins.
        /// </para>
        /// <para>
        /// <b>Ordering contract: place this middleware at the front.</b> Every middleware that consumes
        /// client identity before calling the next one — host filtering, HTTPS redirection, CORS,
        /// authentication, cookie policy, redirect generation, rate limiting — must run <em>after</em>
        /// it, or it will observe the proxy hop instead of the client. Consumers that read after the
        /// pipeline unwinds (HSTS emission, access logging) see the resolved identity wherever they sit,
        /// because the feature stays on the exchange. Only middleware that reads no client identity on
        /// the way in may precede it: <c>UseHttpLogging</c> and <c>UseSecurityHeaders</c>.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseForwardedHeaders()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<ForwardedHeadersMiddleware>().LastOrDefault()
                    ?? throw new InvalidOperationException(ErrorMessages.ForwardedHeadersNotRegistered);

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
