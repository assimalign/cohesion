using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.CookiePolicy.Internal;

namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// Configures the cookie-policy middleware that <c>builder.Services.AddCookiePolicy(policy =&gt; ...)</c>
/// registers and <see cref="CookiePolicyExtensions.UseCookiePolicy(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <see cref="Build"/> validates the options and captures them, so an invalid option fails where the
/// application registers the policy, never per request, and changing the options afterwards has no
/// effect.
/// </remarks>
public sealed class CookiePolicyBuilder
{
    /// <summary>
    /// Initializes a builder with the default policy: <c>Secure</c> whenever the effective request scheme
    /// is HTTPS, the RFC 6265bis requirements upgraded rather than rejected, the 400-day lifetime cap, and
    /// no consent requirement.
    /// </summary>
    public CookiePolicyBuilder()
    {
    }

    /// <summary>
    /// Gets the policy's options: the attribute floors, the RFC 6265bis actions, the lifetime cap, consent
    /// gating for non-essential cookies, and the rejection hook.
    /// </summary>
    public CookiePolicyOptions Options { get; } = new();

    /// <summary>
    /// Builds the cookie-policy middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which replaces each exchange's response cookie feature so that every cookie appended
    /// through <c>context.Response.Cookies</c> is judged before it reaches a <c>Set-Cookie</c> field, and
    /// installs the exchange's <see cref="ICookieConsentFeature"/> when it has none.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// An option is not a defined value, the maximum lifetime is not greater than zero or exceeds 400 days,
    /// the time provider is <see langword="null"/>, or the consent cookie has a <see langword="default"/>
    /// name or value, an empty value, a non-positive <c>Max-Age</c>, or an <c>Expires</c>. The exception's
    /// parameter name is <c>Options</c>.
    /// </exception>
    public IWebApplicationMiddleware Build()
    {
        CookiePolicyRules rules = CookiePolicyRules.Create(Options, nameof(Options));

        return new CookiePolicyMiddleware(rules);
    }
}
