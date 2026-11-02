using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Configures session behavior. This is the documented home for session
/// configuration; it intentionally does not bind the package to any backing
/// store. Cookie emission and store wiring are out of scope for the in-process
/// session surface and are tracked separately.
/// </summary>
public sealed class HttpSessionOptions
{
    /// <summary>
    /// Gets or sets the name of the cookie used to carry the session
    /// identifier when session-cookie wiring is enabled. Defaults to
    /// <c>.Cohesion.Session</c>.
    /// </summary>
    /// <remarks>
    /// The value is a non-empty RFC 6265 <c>token</c>: assigning any other
    /// <see cref="string"/> throws <see cref="ArgumentException"/> from the
    /// <see cref="HttpCookieName"/> conversion, before the setter runs.
    /// <c>AddSessions</c> in <c>Assimalign.Cohesion.Web.Sessions</c> refuses
    /// <see langword="default"/> when it registers the middleware, before any
    /// session is established.
    /// </remarks>
    public HttpCookieName CookieName { get; set; } = ".Cohesion.Session";

    /// <summary>
    /// Gets or sets the path scope for the session cookie. Defaults to
    /// <c>/</c>.
    /// </summary>
    /// <remarks>
    /// The value is an RFC 6265 <c>path-value</c>, US-ASCII with no <c>;</c> and
    /// no control characters: assigning any other <see cref="string"/> throws
    /// <see cref="ArgumentException"/> from the <see cref="HttpCookiePath"/>
    /// conversion, before the setter runs. <c>AddSessions</c> in
    /// <c>Assimalign.Cohesion.Web.Sessions</c> refuses <see langword="default"/>,
    /// which holds no path, when it registers the middleware. The empty string
    /// remains a valid path-value.
    /// </remarks>
    public HttpCookiePath CookiePath { get; set; } = "/";

    /// <summary>
    /// Gets or sets whether the session cookie is marked <c>HttpOnly</c>.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    public bool CookieHttpOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the session cookie is essential to the application, so a cookie-consent
    /// policy emits it before the user has consented. Defaults to <see langword="false"/>: a session
    /// usually holds state that needs consent. Set it to <see langword="true"/> when the application
    /// cannot work without its session, such as a shopping cart or a multi-step form.
    /// </summary>
    public bool CookieIsEssential { get; set; }

    /// <summary>
    /// Gets or sets the idle timeout after which an inactive session is
    /// considered expired. Defaults to 20 minutes.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(20);
}
