using System;

using Assimalign.Cohesion.Web.CookiePolicy.Internal;

namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// Pipeline-builder members that add cookie-policy enforcement to a web application.
/// </summary>
public static class CookiePolicyExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the cookie-policy middleware. From this point in the pipeline, every cookie appended through
        /// <c>context.Response.Cookies</c> is judged before it reaches a <c>Set-Cookie</c> field: consent
        /// for non-essential cookies, the <c>Secure</c>, <c>HttpOnly</c>, and minimum <c>SameSite</c>
        /// floors, the RFC 6265bis requirements, and the lifetime cap. A dropped cookie is reported to
        /// <see cref="CookiePolicyOptions.OnRejected"/>.
        /// </summary>
        /// <param name="configure">
        /// An optional callback that configures the policy. Without one the defaults apply: <c>Secure</c>
        /// whenever the effective request scheme is HTTPS, the RFC 6265bis requirements upgraded rather than
        /// rejected, the 400-day lifetime cap, and no consent requirement.
        /// </param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// An option is not a defined value, the maximum lifetime is not greater than zero or exceeds
        /// 400 days, the time provider is <see langword="null"/>, or the consent cookie is malformed: an
        /// invalid name, an empty or invalid value, a non-positive <c>Max-Age</c>, or an <c>Expires</c>.
        /// </exception>
        /// <remarks>
        /// <para>
        /// Register it early: after <c>UseForwardedHeaders</c> and <c>UseHostFiltering</c>, and before
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
        /// the application reads and changes consent. The options are validated and captured here, at
        /// registration; nothing is resolved per request.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseCookiePolicy(Action<CookiePolicyOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            CookiePolicyOptions options = new();
            configure?.Invoke(options);

            CookiePolicyRules rules = CookiePolicyRules.Create(options, nameof(configure));

            return builder.Use(new CookiePolicyMiddleware(rules));
        }
    }
}
