using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.Web.Authentication.Cookie;

/// <summary>
/// Configures a cookie authentication scheme: the cookie's name and attributes, the interactive
/// redirect paths, the ticket lifetime and sliding-expiration behavior, and the
/// <see cref="IDataProtector"/> that seals the ticket.
/// </summary>
/// <remarks>
/// These options are set once at composition time. The <see cref="TicketProtector"/> is supplied
/// by the composition root (a <c>*.Hosting</c> project) from the application's rotating key ring —
/// the handler never derives keys itself.
/// </remarks>
public sealed class CookieAuthenticationOptions
{
    /// <summary>
    /// Gets or sets the scheme's human-readable display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the cookie name. Defaults to <c>.Cohesion.Cookies</c>.
    /// </summary>
    public string CookieName { get; set; } = CookieAuthenticationDefaults.CookiePrefix + CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>
    /// Gets the template attributes applied to the emitted <c>Set-Cookie</c> (Domain, Path,
    /// Secure, SameSite, HttpOnly). <c>Expires</c>/<c>Max-Age</c> are set per sign-in from the
    /// ticket lifetime and persistence, so any value set here for those is overwritten.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>HttpOnly=true</c>, <c>SameSite=Lax</c>, <c>Path=/</c>. Production
    /// deployments served over HTTPS should set <see cref="HttpCookieOptions.Secure"/> to
    /// <see langword="true"/>.
    /// </remarks>
    public HttpCookieOptions Cookie { get; } = new()
    {
        HttpOnly = true,
        SameSite = HttpCookieSameSiteMode.Lax,
        Path = "/",
    };

    /// <summary>
    /// Gets or sets the path a browser challenge redirects to. Defaults to
    /// <see cref="CookieAuthenticationDefaults.LoginPath"/>.
    /// </summary>
    public string LoginPath { get; set; } = CookieAuthenticationDefaults.LoginPath;

    /// <summary>
    /// Gets or sets the logout path. Defaults to <see cref="CookieAuthenticationDefaults.LogoutPath"/>.
    /// </summary>
    public string LogoutPath { get; set; } = CookieAuthenticationDefaults.LogoutPath;

    /// <summary>
    /// Gets or sets the path a browser forbid redirects to. Defaults to
    /// <see cref="CookieAuthenticationDefaults.AccessDeniedPath"/>.
    /// </summary>
    public string AccessDeniedPath { get; set; } = CookieAuthenticationDefaults.AccessDeniedPath;

    /// <summary>
    /// Gets or sets the query-string parameter that carries the post-login return URL on a
    /// challenge redirect. Defaults to <see cref="CookieAuthenticationDefaults.ReturnUrlParameter"/>.
    /// </summary>
    public string ReturnUrlParameter { get; set; } = CookieAuthenticationDefaults.ReturnUrlParameter;

    /// <summary>
    /// Gets or sets the ticket lifetime used when a sign-in does not specify
    /// <see cref="AuthenticationProperties.ExpiresUtc"/>. Defaults to 14 days.
    /// </summary>
    public TimeSpan ExpireTimeSpan { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Gets or sets whether a ticket past the midpoint of its lifetime is re-issued with a fresh
    /// window on the next authenticate. Defaults to <see langword="true"/>.
    /// </summary>
    public bool SlidingExpiration { get; set; } = true;

    /// <summary>
    /// Gets or sets the protector that seals and verifies the ticket. Required; the composition
    /// root sets it from the application key ring.
    /// </summary>
    public IDataProtector? TicketProtector { get; set; }

    /// <summary>
    /// Gets or sets the time source used for ticket issuance, expiry, and sliding renewal.
    /// Defaults to <see cref="TimeProvider.System"/>. Tests substitute a fake provider to drive
    /// expiry deterministically.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
