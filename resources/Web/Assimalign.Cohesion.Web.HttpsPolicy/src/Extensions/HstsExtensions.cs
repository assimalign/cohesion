using System;
using System.Linq;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.HttpsPolicy.Internal;

namespace Assimalign.Cohesion.Web.HttpsPolicy;

/// <summary>
/// Pipeline-builder extension that places HTTP Strict Transport Security (RFC 6797) emission in the
/// Web application middleware pipeline.
/// </summary>
public static class HstsExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the middleware that emits the <c>Strict-Transport-Security</c> field on secure
        /// responses only (RFC 6797 §7.2), skipping the excluded hosts: the middleware the application
        /// registered with <c>builder.Services.AddHsts(hsts =&gt; ...)</c>, or one with the default policy
        /// when it registered none.
        /// </summary>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The policy is configured where it is registered (<see cref="HstsBuilder"/>); this verb only
        /// positions it. The default policy is a 365-day <c>max-age</c>, neither directive, and loopback
        /// excluded. When the application registered HSTS more than once, the last registration wins.
        /// </para>
        /// <para>
        /// Place this <em>before the exception boundary</em> (and generally near the front of the
        /// pipeline): the field is applied after the pipeline unwinds, so placing the boundary inside
        /// this middleware means a reset error response served over TLS still carries the policy.
        /// </para>
        /// <para>
        /// Connection security is the effective typed scheme
        /// (<see cref="HttpContextForwardedExtensions.EffectiveScheme"/>): the scheme a trusted
        /// TLS-terminating proxy asserted when the forwarded-headers middleware resolved one, otherwise the
        /// transport-derived scheme (#763); there is no scheme-string or header sniffing. The excluded
        /// hosts are matched against the effective host. Both are read after the pipeline unwinds, so
        /// the forwarded identity is honored wherever <c>UseForwardedHeaders</c> sits in the pipeline.
        /// Excluded-host matching reuses <see cref="HttpHostMatcher"/> from the Http core, so it shares
        /// the same case-insensitive, port-ignoring, IPv6-bracket-insensitive host semantics as the rest
        /// of the stack.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseHsts()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<HstsMiddleware>().LastOrDefault()
                    ?? new HstsBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
