using System;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// Configures the cookie policy that <c>UseCookiePolicy</c> applies to every cookie the application
/// appends to a response: the attribute floors, the RFC 6265bis requirements, the lifetime cap, and
/// consent gating for non-essential cookies.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseCookiePolicy</c> validates the options and copies them once, at registration. Changing this
/// instance afterwards has no effect, and nothing is resolved per request.
/// </para>
/// <para>
/// The policy judges a cookie when it is appended and applies its rules in this order:
/// </para>
/// <list type="number">
/// <item><description>Consent: a non-essential cookie that is not a deletion is dropped while the request needs consent and has none.</description></item>
/// <item><description>The floors: <see cref="Secure"/>, <see cref="HttpOnly"/>, and <see cref="MinimumSameSitePolicy"/>.</description></item>
/// <item><description>The RFC 6265bis requirements: <c>SameSite=None</c> requires <c>Secure</c>, then the <c>__Host-</c> and <c>__Secure-</c> prefixes.</description></item>
/// <item><description>The lifetime cap, <see cref="MaxLifetime"/>.</description></item>
/// </list>
/// <para>
/// No rule removes protection the application asked for: <c>Secure</c> and <c>HttpOnly</c> are never
/// cleared and <c>SameSite</c> is never lowered.
/// </para>
/// </remarks>
public sealed class CookiePolicyOptions
{
    /// <summary>
    /// The default name of the cookie that records consent (<c>.Cohesion.Consent</c>).
    /// </summary>
    public const string DefaultConsentCookieName = ".Cohesion.Consent";

    /// <summary>
    /// The default value of the cookie that records consent (<c>yes</c>).
    /// </summary>
    public const string DefaultConsentCookieValue = "yes";

    /// <summary>
    /// Gets or sets the lowest <c>SameSite</c> mode a cookie may carry. A cookie below it is raised to
    /// it, in the order <see cref="HttpCookieSameSiteMode.Unspecified"/>,
    /// <see cref="HttpCookieSameSiteMode.None"/>, <see cref="HttpCookieSameSiteMode.Lax"/>,
    /// <see cref="HttpCookieSameSiteMode.Strict"/>. Defaults to
    /// <see cref="HttpCookieSameSiteMode.Unspecified"/>, which raises nothing.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpCookieSameSiteMode.Lax"/> is the usual choice for an application that issues no
    /// cross-site cookies: it also raises an explicit <c>SameSite=None</c>, which breaks an embed or a
    /// federated sign-in that depends on one. <see cref="HttpCookieSameSiteMode.Strict"/> also withholds
    /// cookies, an authentication cookie included, from a top-level navigation that arrives from another
    /// site.
    /// </remarks>
    public HttpCookieSameSiteMode MinimumSameSitePolicy { get; set; } = HttpCookieSameSiteMode.Unspecified;

    /// <summary>
    /// Gets or sets when the policy adds <c>Secure</c>. Defaults to
    /// <see cref="CookieSecurePolicy.SameAsRequest"/>: every cookie issued to a client that reached the
    /// application over HTTPS, directly or through a trusted TLS-terminating proxy, is <c>Secure</c>.
    /// </summary>
    public CookieSecurePolicy Secure { get; set; } = CookieSecurePolicy.SameAsRequest;

    /// <summary>
    /// Gets or sets whether the policy adds <c>HttpOnly</c>. Defaults to
    /// <see cref="CookieHttpOnlyPolicy.None"/>.
    /// </summary>
    public CookieHttpOnlyPolicy HttpOnly { get; set; } = CookieHttpOnlyPolicy.None;

    /// <summary>
    /// Gets or sets what the policy does with a <c>SameSite=None</c> cookie that is not <c>Secure</c>,
    /// which a user agent ignores (RFC 6265bis &#167; 5.7). Defaults to
    /// <see cref="CookieViolationAction.Upgrade"/>, which adds <c>Secure</c>.
    /// </summary>
    public CookieViolationAction SameSiteNoneWithoutSecure { get; set; } = CookieViolationAction.Upgrade;

    /// <summary>
    /// Gets or sets what the policy does with a cookie whose name prefix promises attributes it lacks: a
    /// <c>__Secure-</c> cookie without <c>Secure</c>, or a <c>__Host-</c> cookie without <c>Secure</c>,
    /// without <c>Path=/</c>, or with a <c>Domain</c> (RFC 6265bis &#167; 4.1.3). Defaults to
    /// <see cref="CookieViolationAction.Upgrade"/>.
    /// </summary>
    /// <remarks>
    /// A user agent matches the prefixes case-insensitively and ignores a cookie that breaks one
    /// (RFC 6265bis &#167; 5.4 and &#167; 5.7), so the policy matches them the same way:
    /// <c>__HOST-id</c> is held to the <c>__Host-</c> rules. Upgrading a <c>__Host-</c> cookie adds
    /// <c>Secure</c>, sets <c>Path=/</c>, and removes <c>Domain</c>, which is what the name declares.
    /// </remarks>
    public CookieViolationAction PrefixViolation { get; set; } = CookieViolationAction.Upgrade;

    /// <summary>
    /// Gets or sets the longest lifetime a cookie may claim. A longer <c>Max-Age</c> is reduced to it,
    /// and an <c>Expires</c> further out than the current time plus this value is pulled back to that
    /// instant. Defaults to <see cref="HttpCookieLimits.DefaultMaxLifetime"/>, the 400 days a user agent
    /// enforces (RFC 6265bis &#167; 5.5).
    /// </summary>
    /// <remarks>
    /// The cap is applied when the cookie is appended, against <see cref="TimeProvider"/>, through
    /// <see cref="HttpCookie.ClampLifetime(DateTimeOffset, TimeSpan)"/>. A deletion, a zero or negative
    /// <c>Max-Age</c> or an <c>Expires</c> in the past, is never changed. The value must be greater than
    /// zero and at most 400 days: a user agent caps every lifetime at 400 days, so a longer cap would let
    /// the response claim a lifetime no client honors.
    /// </remarks>
    public TimeSpan MaxLifetime { get; set; } = HttpCookieLimits.DefaultMaxLifetime;

    /// <summary>
    /// Gets or sets the clock the policy reads when it caps a lifetime and when it decides whether an
    /// <c>Expires</c> cookie is a deletion. Defaults to <see cref="TimeProvider.System"/>; tests
    /// substitute a fixed clock.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets the predicate that decides whether a request needs the user's consent before
    /// non-essential cookies are issued. It runs at most once per exchange, the first time a
    /// non-essential cookie is appended or <see cref="ICookieConsentFeature.IsConsentNeeded"/> is read.
    /// <see langword="null"/>, the default, means consent is never needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While consent is needed and not given, the policy drops every cookie that is neither essential
    /// (<see cref="HttpCookieOptions.IsEssential"/>) nor a deletion, and reports each one with
    /// <see cref="CookiePolicyRejectionReason.ConsentRequired"/>.
    /// </para>
    /// <para>
    /// The predicate and the consent cookie options configure the <see cref="ICookieConsentFeature"/>
    /// the policy installs. They do not apply to an exchange that already carries a consent feature when
    /// the policy runs: one an outer <c>UseCookiePolicy</c> installed, or one the application supplied.
    /// </para>
    /// </remarks>
    public Func<IHttpContext, bool>? CheckConsentNeeded { get; set; }

    /// <summary>
    /// Gets or sets the name of the cookie that records consent. Defaults to
    /// <see cref="DefaultConsentCookieName"/>. It must be a valid RFC 6265 cookie name.
    /// </summary>
    public string ConsentCookieName { get; set; } = DefaultConsentCookieName;

    /// <summary>
    /// Gets or sets the value the consent cookie carries; a request has consent when its consent cookie
    /// carries exactly this value. Defaults to <see cref="DefaultConsentCookieValue"/>. It must be a
    /// non-empty RFC 6265 cookie value.
    /// </summary>
    public string ConsentCookieValue { get; set; } = DefaultConsentCookieValue;

    /// <summary>
    /// Gets the attributes of the consent cookie that <see cref="ICookieConsentFeature.GrantConsent"/>
    /// appends. Defaults to <c>Path=/</c>, <c>SameSite=Lax</c>, and a 365-day <c>Max-Age</c>.
    /// </summary>
    /// <remarks>
    /// The consent cookie is always essential, whatever <see cref="HttpCookieOptions.IsEssential"/> says
    /// here, because it is the record of consent itself. It is not <c>HttpOnly</c> by default so that
    /// client-side script can read the decision before it loads anything that tracks. The policy's floors
    /// still apply to it. Set its lifetime with <see cref="HttpCookieOptions.MaxAge"/>, which must be
    /// positive when set; an absolute <see cref="HttpCookieOptions.Expires"/> is rejected at registration,
    /// because one fixed date would be stamped on every grant.
    /// </remarks>
    public HttpCookieOptions ConsentCookie { get; } = new()
    {
        Path = "/",
        SameSite = HttpCookieSameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(365),
        IsEssential = true,
    };

    /// <summary>
    /// Gets or sets a hook the policy calls each time it drops a cookie, with the cookie as the
    /// application appended it and the reason. <see langword="null"/>, the default, drops silently.
    /// </summary>
    /// <remarks>
    /// The hook runs synchronously inside the call that appended the cookie, so it must be fast and must
    /// not throw: an exception propagates to the code that appended the cookie. It observes the decision
    /// and cannot reverse it.
    /// </remarks>
    public Action<CookiePolicyRejectionContext>? OnRejected { get; set; }
}
