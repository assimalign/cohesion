using System;
using System.Linq;

using Assimalign.Cohesion.Web.CookiePolicy.Internal;

namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// Pipeline-builder extension that places cookie-policy enforcement in a web application's middleware
/// pipeline.
/// </summary>
public static class CookiePolicyExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the cookie-policy middleware the application registered with
        /// <c>builder.Services.AddCookiePolicy(policy =&gt; ...)</c>, or one with the default policy when it
        /// registered none. From this point in the pipeline, every cookie appended through
        /// <c>context.Response.Cookies</c> is judged before it reaches a <c>Set-Cookie</c> field: consent
        /// for non-essential cookies, the <c>Secure</c>, <c>HttpOnly</c>, and minimum <c>SameSite</c>
        /// floors, the RFC 6265bis requirements, and the lifetime cap. A dropped cookie is reported to
        /// <see cref="CookiePolicyOptions.OnRejected"/>.
        /// </summary>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The policy is configured where it is registered (<see cref="CookiePolicyBuilder"/>), which
        /// validates and captures the options; this verb only positions it, and nothing is resolved per
        /// request. When the application registered the policy more than once, the last registration wins.
        /// The default policy adds <c>Secure</c> whenever the effective request scheme is HTTPS, upgrades
        /// rather than rejects the RFC 6265bis violations, applies the 400-day lifetime cap, and requires no
        /// consent.
        /// </para>
        /// <para>
        /// Place it early: after <c>UseForwardedHeaders</c> and <c>UseHostFiltering</c>, and before
        /// <c>UseRouting</c>, <c>UseSessions</c>, <c>UseAuthentication</c>, and anything else that writes
        /// cookies. The middleware replaces the exchange's response cookie feature and keeps the feature in
        /// place after <c>next</c> returns, so a cookie is judged whenever it is appended, before or after
        /// this point. Cookies that earlier middleware already queued are judged when the policy takes
        /// over. A <c>Set-Cookie</c> field written directly to <c>context.Response.Headers</c> bypasses the
        /// cookie model and therefore the policy; write cookies through <c>context.Response.Cookies</c>.
        /// </para>
        /// <para>
        /// The <c>Secure</c> decision reads the effective request scheme when a cookie is appended. Behind a
        /// TLS-terminating proxy, register <c>UseForwardedHeaders</c> so that the proxy's <c>https</c>
        /// counts; without it, or for a peer outside its trust model, the effective scheme is the
        /// transport's own.
        /// </para>
        /// <para>
        /// The middleware installs an <see cref="ICookieConsentFeature"/> on each exchange, through which
        /// the application reads and changes consent.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseCookiePolicy()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<CookiePolicyMiddleware>().LastOrDefault()
                    ?? new CookiePolicyBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
