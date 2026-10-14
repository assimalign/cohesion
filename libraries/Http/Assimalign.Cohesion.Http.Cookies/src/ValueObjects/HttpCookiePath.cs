using System;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The value of a cookie's <c>Path</c> attribute: an RFC 6265 &#167; 4.1.1
/// <c>path-value</c>, US-ASCII with no control character and no <c>;</c>. The
/// empty string is a valid path-value.
/// </summary>
/// <remarks>
/// The path is checked when the value is constructed, including through the
/// implicit conversions from <see cref="string"/>, so a path built from untrusted
/// input can never add an attribute to, or corrupt, the <c>Set-Cookie</c> line.
/// <see langword="default"/> holds no path, and <see cref="HttpCookie.ToString"/>
/// omits the attribute for it.
/// </remarks>
public partial struct HttpCookiePath
{
    /// <summary>
    /// Determines whether <paramref name="value"/> is an RFC 6265 &#167; 4.1.1
    /// <c>path-value</c>.
    /// </summary>
    /// <param name="value">The candidate path.</param>
    /// <param name="message">The reason the path is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when every character lies in <c>0x20</c>&#8211;<c>0x7E</c> and none is <c>;</c>.</returns>
    public partial bool IsValid(string value, out string? message)
    {
        // The value is not echoed: it may hold the control character that made it invalid.
        if (value is null || !HttpCookieGrammar.IsValidPath(value))
        {
            message = ErrorMessages.CookiePathInvalid;
            return false;
        }

        message = null;
        return true;
    }

    /// <summary>
    /// Converts an optional path to an optional <see cref="HttpCookiePath"/>,
    /// keeping <see langword="null"/> as <see langword="null"/>.
    /// </summary>
    /// <param name="value">The path, or <see langword="null"/> for no <c>Path</c> attribute.</param>
    /// <returns>The checked path, or <see langword="null"/> when <paramref name="value"/> is <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a <c>path-value</c>.</exception>
    /// <remarks>
    /// Without this conversion, a <see langword="null"/> string assigned to
    /// <see cref="HttpCookieOptions.Path"/> would go through the generated
    /// <c>HttpCookiePath(string)</c> conversion and be refused, instead of
    /// clearing the attribute.
    /// </remarks>
    public static implicit operator HttpCookiePath?(string? value)
    {
        // A conditional expression would convert the null literal through HttpCookiePath(string).
        if (value is null)
        {
            return null;
        }

        return new HttpCookiePath(value);
    }
}
