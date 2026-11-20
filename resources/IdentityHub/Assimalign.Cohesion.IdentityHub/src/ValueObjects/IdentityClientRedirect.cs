using System;

using Assimalign.Cohesion.IdentityHub.Internal;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One redirect URI of an app registration: where the hub may send a user back to after the
/// authorization code grant, and the kind of application that receives them there.
/// </summary>
/// <remarks>
/// <para>
/// <b>A redirect URI is matched as an exact string.</b> The hub compares the URI a request
/// names with <see cref="Uri"/> character for character, as OAuth 2.1 requires, so the URI is
/// kept as it was written and is never normalized. A <c>*</c> is refused, because it would be
/// read as a wildcard and matched as a literal.
/// </para>
/// <para>
/// A URI is 1 to 512 characters that RFC 3986 allows in a URI (so no space, quote, angle
/// bracket, or backslash), absolute, with no fragment and no user information. On every
/// platform it may be <c>https</c>, or <c>http</c> when its host is written as
/// <c>localhost</c> or as a loopback address. A <see cref="IdentityClientPlatform.Native"/>
/// redirect may also use a private-use scheme that holds a <c>.</c>, the reversed domain name
/// of RFC 8252 section 7.1 (<c>com.example.app:/callback</c>). Every other scheme is refused.
/// </para>
/// <para>
/// <see langword="default"/> holds no URI and every registration boundary refuses it.
/// </para>
/// </remarks>
public readonly record struct IdentityClientRedirect
{
    /// <summary>
    /// The longest redirect URI, in characters.
    /// </summary>
    public const int MaxLength = IdentityClientGrammar.MaxRedirectLength;

    /// <summary>
    /// Initializes a redirect.
    /// </summary>
    /// <param name="uri">The redirect URI, exactly as a request will name it.</param>
    /// <param name="platform">The kind of application the URI returns to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="platform"/> is not a platform.</exception>
    /// <exception cref="ArgumentException"><paramref name="uri"/> breaks a rule of its platform.</exception>
    public IdentityClientRedirect(string uri, IdentityClientPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsPlatform(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform), platform, ErrorMessages.RedirectPlatformInvalid);
        }

        string? refusal = IdentityClientGrammar.CheckRedirect(uri, platform);
        if (refusal is not null)
        {
            throw new ArgumentException(refusal, nameof(uri));
        }

        Uri = uri;
        Platform = platform;
    }

    /// <summary>
    /// Gets the redirect URI, exactly as it was written.
    /// </summary>
    public string Uri { get; }

    /// <summary>
    /// Gets the kind of application the URI returns to.
    /// </summary>
    public IdentityClientPlatform Platform { get; }

    /// <summary>
    /// Creates a redirect to a web application that redeems the authorization code on its server.
    /// </summary>
    /// <param name="uri">The redirect URI, exactly as a request will name it.</param>
    /// <returns>The redirect.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="uri"/> breaks a rule of the platform.</exception>
    public static IdentityClientRedirect Web(string uri) => new(uri, IdentityClientPlatform.Web);

    /// <summary>
    /// Creates a redirect to a single-page application that redeems the authorization code in
    /// the browser.
    /// </summary>
    /// <param name="uri">The redirect URI, exactly as a request will name it.</param>
    /// <returns>The redirect.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="uri"/> breaks a rule of the platform.</exception>
    public static IdentityClientRedirect SinglePage(string uri) => new(uri, IdentityClientPlatform.SinglePage);

    /// <summary>
    /// Creates a redirect to a mobile or desktop application.
    /// </summary>
    /// <param name="uri">The redirect URI, exactly as a request will name it.</param>
    /// <returns>The redirect.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="uri"/> breaks a rule of the platform.</exception>
    public static IdentityClientRedirect Native(string uri) => new(uri, IdentityClientPlatform.Native);

    /// <summary>
    /// Creates a redirect when the URI follows the rules of its platform.
    /// </summary>
    /// <param name="uri">The candidate redirect URI.</param>
    /// <param name="platform">The kind of application the URI returns to.</param>
    /// <param name="redirect">The redirect, or <see langword="default"/> when it is refused.</param>
    /// <returns><see langword="true"/> when the redirect was created.</returns>
    public static bool TryCreate(string? uri, IdentityClientPlatform platform, out IdentityClientRedirect redirect)
    {
        if (uri is null || !IsPlatform(platform) || IdentityClientGrammar.CheckRedirect(uri, platform) is not null)
        {
            redirect = default;
            return false;
        }

        redirect = new IdentityClientRedirect(uri, platform);
        return true;
    }

    /// <summary>
    /// Returns the redirect URI.
    /// </summary>
    /// <returns>The redirect URI, or an empty string for a <see langword="default"/> redirect.</returns>
    public override string ToString() => Uri ?? string.Empty;

    private static bool IsPlatform(IdentityClientPlatform platform)
    {
        return platform is IdentityClientPlatform.Web or IdentityClientPlatform.SinglePage or IdentityClientPlatform.Native;
    }
}
