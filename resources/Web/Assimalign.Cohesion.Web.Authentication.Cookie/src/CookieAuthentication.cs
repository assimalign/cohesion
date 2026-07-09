using System;

namespace Assimalign.Cohesion.Web.Authentication.Cookie;

/// <summary>
/// Factory for the cookie authentication handler. The composition root (a <c>*.Hosting</c>
/// project) calls <see cref="CreateHandler(CookieAuthenticationOptions)"/> from a scheme's handler
/// factory; the concrete handler stays internal so the interface (<see cref="IAuthenticationHandler"/>)
/// remains the only public surface.
/// </summary>
public static class CookieAuthentication
{
    /// <summary>
    /// Creates a cookie authentication handler over the supplied options.
    /// </summary>
    /// <param name="options">The configured cookie options. <see cref="CookieAuthenticationOptions.TicketProtector"/> must be set.</param>
    /// <returns>A cookie handler as an <see cref="IAuthenticationSignInHandler"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="CookieAuthenticationOptions.TicketProtector"/> is not set.</exception>
    public static IAuthenticationSignInHandler CreateHandler(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CookieAuthenticationHandler(options);
    }
}
