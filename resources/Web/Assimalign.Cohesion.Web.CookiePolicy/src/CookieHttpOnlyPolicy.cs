namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// Selects whether the cookie policy adds the <c>HttpOnly</c> attribute to a cookie the application
/// appends to the response.
/// </summary>
/// <remarks>
/// The policy is a floor. It can add <c>HttpOnly</c> but never removes it.
/// </remarks>
public enum CookieHttpOnlyPolicy
{
    /// <summary>
    /// Leaves <c>HttpOnly</c> as the application set it. This is the default, because client-side script
    /// legitimately reads some cookies, such as a double-submit antiforgery token or the consent cookie.
    /// </summary>
    None = 0,

    /// <summary>
    /// Adds <c>HttpOnly</c> to every cookie, so no cookie the application issues is readable through
    /// <c>document.cookie</c>.
    /// </summary>
    Always = 1,
}
