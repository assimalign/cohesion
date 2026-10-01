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
    public string CookieName { get; set; } = ".Cohesion.Session";

    /// <summary>
    /// Gets or sets the path scope for the session cookie. Defaults to
    /// <c>/</c>.
    /// </summary>
    public string CookiePath { get; set; } = "/";

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
