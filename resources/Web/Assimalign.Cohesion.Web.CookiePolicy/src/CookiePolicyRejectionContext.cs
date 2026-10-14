using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// Describes a cookie the cookie policy dropped. The policy hands one to
/// <see cref="CookiePolicyOptions.OnRejected"/> each time it drops a cookie.
/// </summary>
/// <remarks>
/// The hook observes the decision; it cannot reverse it. A dropped cookie never reaches the response:
/// it is absent from <c>response.Cookies</c> and from the <c>Set-Cookie</c> fields.
/// </remarks>
public sealed class CookiePolicyRejectionContext
{
    internal CookiePolicyRejectionContext(IHttpContext context, HttpCookie cookie, CookiePolicyRejectionReason reason)
    {
        Context = context;
        Cookie = cookie;
        Reason = reason;
    }

    /// <summary>
    /// Gets the exchange whose response the cookie was appended to.
    /// </summary>
    public IHttpContext Context { get; }

    /// <summary>
    /// Gets the cookie as the application appended it, before the policy changed any attribute.
    /// </summary>
    public HttpCookie Cookie { get; }

    /// <summary>
    /// Gets the rule that dropped the cookie.
    /// </summary>
    public CookiePolicyRejectionReason Reason { get; }
}
