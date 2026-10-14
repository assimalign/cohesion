using System;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The value of a cookie's <c>Domain</c> attribute: an RFC 6265 &#167; 4.1.1
/// <c>domain-value</c>, dot-separated labels of letters, digits, and hyphens,
/// optionally preceded by one dot.
/// </summary>
/// <remarks>
/// The domain is checked when the value is constructed, including through the
/// implicit conversions from <see cref="string"/>, so a domain built from
/// untrusted input, such as a forwarded host, can never add an attribute to, or
/// corrupt, the <c>Set-Cookie</c> line. A constructed domain is never empty.
/// <see langword="default"/> holds no domain, and <see cref="HttpCookie.ToString"/>
/// omits the attribute for it. Equality, hashing, and ordering use ordinal,
/// case-insensitive comparison, matching RFC 6265 &#167; 5.1.3.
/// </remarks>
public partial struct HttpCookieDomain
{
    /// <summary>
    /// Determines whether <paramref name="value"/> is an RFC 6265 &#167; 4.1.1
    /// <c>domain-value</c>.
    /// </summary>
    /// <param name="value">The candidate domain.</param>
    /// <param name="message">The reason the domain is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns>
    /// <see langword="true"/> when, after one optional leading dot, the value is
    /// one or more dot-separated labels of at most 63 letters, digits, and
    /// hyphens, and is at most 253 characters long.
    /// </returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the control character that made it invalid.
        if (value is null || !HttpCookieGrammar.IsValidDomain(value))
        {
            message = ErrorMessages.CookieDomainInvalid;
            return false;
        }

        message = null;
        return true;
    }

    /// <summary>
    /// Converts an optional domain to an optional <see cref="HttpCookieDomain"/>,
    /// reading <see langword="null"/> and the empty string as no domain.
    /// </summary>
    /// <param name="value">The domain, or <see langword="null"/> or empty for no <c>Domain</c> attribute.</param>
    /// <returns>The checked domain, or <see langword="null"/> when <paramref name="value"/> is <see langword="null"/> or empty.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not empty and not a <c>domain-value</c>.</exception>
    /// <remarks>
    /// An empty <c>Domain</c> has always meant "no attribute" to
    /// <see cref="HttpCookieOptions.Domain"/>, as RFC 6265 &#167; 5.2.3 tells a user
    /// agent to read an empty <c>Domain=</c>. Without this conversion, a
    /// <see langword="null"/> or empty string would go through the generated
    /// <c>HttpCookieDomain(string)</c> conversion and be refused.
    /// </remarks>
    public static implicit operator HttpCookieDomain?(string? value)
    {
        // A conditional expression would convert the null literal through HttpCookieDomain(string).
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return new HttpCookieDomain(value);
    }
}
