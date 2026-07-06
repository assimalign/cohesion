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
/// other's tokens.
/// </para>
/// <para>
/// Restart-stable and multi-node deployments should set <see cref="Protector"/>
/// to an implementation backed by a persisted, rotating key ring (see the
/// migration note on that property) rather than hand-distributing a shared
/// <see cref="Key"/>. When <see cref="Protector"/> is set, <see cref="Key"/> is ignored.
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
    /// Gets or sets the symmetric key used by the built-in HMAC-SHA256 protector to sign
    /// tokens. Defaults to a per-process random 32-byte key. Used only when
    /// <see cref="Protector"/> is <see langword="null"/>; prefer <see cref="Protector"/> over
    /// hand-distributing this key for multi-instance or restart-stable deployments.
    /// </summary>
    public byte[] Key { get; set; } = RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// Gets or sets the pluggable cryptographic protector that mints and verifies token
    /// payloads. When set, it supersedes <see cref="Key"/> entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Leave this <see langword="null"/> for the zero-config single-process default (an
    /// HMAC-SHA256 protector over <see cref="Key"/>). To make tokens survive restarts and
    /// validate across nodes, set it — typically at builder time in a <c>*.Hosting</c>
    /// project — to an implementation backed by a persisted, rotating key ring such as the
    /// Cohesion data-protection provider.
    /// </para>
    /// <para>
    /// <b>Migration from <see cref="Key"/>.</b> Deployments that previously shared a static
    /// <see cref="Key"/> across instances should instead point every node at a shared key
    /// repository and assign a ring-backed <see cref="Protector"/>; the ring then handles
    /// persistence, rotation, and grace-period validation, so raw key bytes no longer need to
    /// be copied between nodes. This package takes no dependency on any data-protection
    /// library — the composition root adapts one to <see cref="IHttpAntiforgeryProtector"/>.
    /// </para>
    /// </remarks>
    public IHttpAntiforgeryProtector? Protector { get; set; }

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
