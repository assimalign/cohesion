namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// Selects what the cookie policy does with a cookie that breaks an RFC 6265bis requirement which a
/// user agent enforces by ignoring the cookie: <c>SameSite=None</c> without <c>Secure</c>, or a
/// <c>__Secure-</c> or <c>__Host-</c> name prefix whose attributes do not match it.
/// </summary>
/// <remarks>
/// Both actions keep the response honest. A user agent would discard the cookie anyway, so the policy
/// either repairs it into a cookie the user agent accepts or drops it where the application can observe
/// the drop, through <see cref="CookiePolicyOptions.OnRejected"/>.
/// </remarks>
public enum CookieViolationAction
{
    /// <summary>
    /// Adds the attributes the requirement needs, so the user agent accepts the cookie: <c>Secure</c>,
    /// and for a <c>__Host-</c> name also <c>Path=/</c> with no <c>Domain</c>. This is the default.
    /// </summary>
    Upgrade = 0,

    /// <summary>
    /// Drops the cookie and reports it to <see cref="CookiePolicyOptions.OnRejected"/>.
    /// </summary>
    Reject = 1,
}
