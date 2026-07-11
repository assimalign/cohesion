using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Internal;

namespace Assimalign.Cohesion.Web;

public static partial class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the forwarded-headers middleware, which resolves the effective client
        /// address, scheme, and host from the RFC 7239 <c>Forwarded</c> and/or
        /// <c>X-Forwarded-*</c> headers under the trust model in
        /// <paramref name="configure"/>, and attaches the result to the exchange as an
        /// <see cref="IHttpForwardedFeature"/>. Raw request headers and the wire-level
        /// scheme/host/connection surfaces are never mutated — downstream code reads the
        /// resolved values through the feature or the <c>context.Effective*</c> members.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ordering contract: register this middleware first.</b> Every middleware
        /// that consumes client identity — CORS, authentication, cookie policy, redirect
        /// generation, rate limiting, access logging — must run <em>after</em> it, or it
        /// will observe the proxy hop instead of the client. Until the repo-wide
        /// middleware-ordering rules land (#26/#145), ordering is by registration order:
        /// make this the first <c>Use</c> call on the pipeline.
        /// </para>
        /// <para>
        /// The options are validated and snapshotted here, at composition time — there is
        /// no request-time configuration lookup, and mutating
        /// <paramref name="configure"/>'s options object afterwards has no effect.
        /// </para>
        /// </remarks>
        /// <param name="configure">Configures the trust model. <see cref="ForwardedHeadersOptions.Headers"/> must select at least one header.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown when the configured <see cref="ForwardedHeadersOptions.Headers"/> is <see cref="ForwardedHeaders.None"/> or a trust list contains a null entry.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the configured <see cref="ForwardedHeadersOptions.ForwardLimit"/> is less than 1.</exception>
        public IWebApplicationPipelineBuilder UseForwardedHeaders(Action<ForwardedHeadersOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new ForwardedHeadersOptions();
            configure.Invoke(options);

            return builder.UseForwardedHeaders(options);
        }

        /// <summary>
        /// Adds the forwarded-headers middleware with the supplied trust-model options.
        /// See <see cref="UseForwardedHeaders(Action{ForwardedHeadersOptions})"/> for the
        /// resolution semantics and the first-position ordering contract.
        /// </summary>
        /// <param name="options">The trust-model options. Validated and snapshotted at composition time.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown when <see cref="ForwardedHeadersOptions.Headers"/> is <see cref="ForwardedHeaders.None"/> or a trust list contains a null entry.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <see cref="ForwardedHeadersOptions.ForwardLimit"/> is less than 1.</exception>
        public IWebApplicationPipelineBuilder UseForwardedHeaders(ForwardedHeadersOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(options);

            return builder.Use(new ForwardedHeadersMiddleware(new ForwardedHeadersResolver(options)));
        }
    }
}
