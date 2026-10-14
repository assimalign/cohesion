namespace Assimalign.Cohesion.Web.CookiePolicy;

/// <summary>
/// Selects when the cookie policy adds the <c>Secure</c> attribute to a cookie the application
/// appends to the response.
/// </summary>
/// <remarks>
/// The policy is a floor. It can add <c>Secure</c> but never removes it, so a cookie the application
/// marked <c>Secure</c> stays <c>Secure</c> under every value.
/// </remarks>
public enum CookieSecurePolicy
{
    /// <summary>
    /// Adds <c>Secure</c> when the client reached the application over HTTPS. The decision reads the
    /// effective request scheme: the scheme a trusted TLS-terminating proxy asserted when
    /// <c>UseForwardedHeaders</c> resolved one, and otherwise the transport's own scheme. This is the
    /// default.
    /// </summary>
    SameAsRequest = 0,

    /// <summary>
    /// Adds <c>Secure</c> to every cookie. Use it when every client connects over HTTPS but the
    /// application cannot see that, for example behind a TLS-terminating proxy that no forwarded-headers
    /// trust model resolves. A user agent ignores a <c>Secure</c> cookie that arrives over plaintext
    /// HTTP (RFC 6265bis &#167; 5.7) unless it treats the origin as secure, as browsers do for
    /// <c>localhost</c>.
    /// </summary>
    Always = 1,

    /// <summary>
    /// Leaves <c>Secure</c> as the application set it.
    /// </summary>
    None = 2,
}
