namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// Why the cookie policy dropped a cookie, reported through
/// <see cref="CookiePolicyRejectionContext.Reason"/>.
/// </summary>
public enum CookiePolicyRejectionReason
{
    /// <summary>
    /// The request needs consent before non-essential cookies are issued, it has none, and the cookie
    /// is neither essential (<c>HttpCookieOptions.IsEssential</c>) nor a deletion.
    /// </summary>
    ConsentRequired = 0,

    /// <summary>
    /// The cookie is <c>SameSite=None</c> without <c>Secure</c>, and
    /// <see cref="CookiePolicyOptions.SameSiteNoneWithoutSecure"/> is
    /// <see cref="CookieViolationAction.Reject"/>.
    /// </summary>
    SameSiteNoneWithoutSecure = 1,

    /// <summary>
    /// The cookie's name starts with <c>__Secure-</c> but the cookie is not <c>Secure</c>, and
    /// <see cref="CookiePolicyOptions.PrefixViolation"/> is <see cref="CookieViolationAction.Reject"/>.
    /// </summary>
    SecurePrefixViolation = 2,

    /// <summary>
    /// The cookie's name starts with <c>__Host-</c> but the cookie is not <c>Secure</c>, its path is not
    /// <c>/</c>, or it carries a <c>Domain</c>, and <see cref="CookiePolicyOptions.PrefixViolation"/> is
    /// <see cref="CookieViolationAction.Reject"/>.
    /// </summary>
    HostPrefixViolation = 3,
}
