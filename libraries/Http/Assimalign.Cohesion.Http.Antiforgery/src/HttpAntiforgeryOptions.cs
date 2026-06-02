using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Configures the antiforgery token names, cookie attributes, and the
/// application HMAC key used to sign request tokens.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Key"/> defaults to a fresh 32-byte cryptographically random
/// value generated when the options are constructed. That default is suitable
/// for a single process: tokens minted before a restart will not validate
/// afterward, and multiple instances behind a load balancer will reject each
/// other's tokens. Deployments that span processes or restarts MUST set
/// <see cref="Key"/> to a shared, securely stored value.
/// </para>
/// </remarks>
public sealed class HttpAntiforgeryOptions
{
    /// <summary>
    /// Gets or sets the name of the cookie that stores the cookie token.
    /// Defaults to <c>__cohesion-antiforgery</c>.
    /// </summary>
    public string CookieName { get; set; } = "__cohesion-antiforgery";

    /// <summary>
    /// Gets or sets the form field that carries the request token for
    /// classic <c>&lt;form&gt;</c> posts. Defaults to
    /// <c>__RequestVerificationToken</c>.
    /// </summary>
    public string FormFieldName { get; set; } = "__RequestVerificationToken";

    /// <summary>
    /// Gets or sets the request header that carries the request token for
    /// AJAX/SPA clients. Defaults to <c>X-CSRF-TOKEN</c>.
    /// </summary>
    public string HeaderName { get; set; } = "X-CSRF-TOKEN";

    /// <summary>
    /// Gets or sets the symmetric key used to sign request tokens with
    /// HMAC-SHA256. Defaults to a per-process random 32-byte key; override for
    /// multi-instance or restart-stable deployments.
    /// </summary>
    public byte[] Key { get; set; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Gets or sets whether the cookie token is marked <c>HttpOnly</c>.
    /// Defaults to <see langword="true"/> so client script cannot read it.
    /// </summary>
    public bool CookieHttpOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the cookie token is marked <c>Secure</c>.
    /// Defaults to <see langword="false"/> so the cookie flows over plain HTTP
    /// in development; set to <see langword="true"/> in production.
    /// </summary>
    public bool CookieSecure { get; set; }

    /// <summary>
    /// Gets or sets the <c>SameSite</c> mode applied to the cookie token.
    /// Defaults to <see cref="HttpCookieSameSiteMode.Strict"/>.
    /// </summary>
    public HttpCookieSameSiteMode CookieSameSite { get; set; } = HttpCookieSameSiteMode.Strict;

    /// <summary>
    /// Gets or sets the path scope applied to the cookie token. Defaults to
    /// <c>/</c>.
    /// </summary>
    public string CookiePath { get; set; } = "/";
}
