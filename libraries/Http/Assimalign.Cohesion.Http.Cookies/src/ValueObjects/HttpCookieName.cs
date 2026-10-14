using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An HTTP cookie name: a non-empty RFC 6265 &#167; 4.1.1 <c>cookie-name</c>,
/// which is an RFC 9110 <c>token</c>.
/// </summary>
/// <remarks>
/// The name is checked when the value is constructed, including through the
/// implicit conversion from <see cref="string"/>, which throws
/// <see cref="System.ArgumentException"/> for <see langword="null"/>, the empty
/// string, or any character outside the token grammar. A constructed name
/// therefore never carries an octet that would split or corrupt a <c>Cookie</c>
/// or <c>Set-Cookie</c> line. <see langword="default"/> holds no name, and
/// <see cref="HttpCookie"/> refuses it.
/// </remarks>
public partial struct HttpCookieName
{
    /// <summary>
    /// Determines whether <paramref name="value"/> is a non-empty RFC 6265
    /// &#167; 4.1.1 <c>cookie-name</c>.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The reason the name is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a non-empty token.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // RFC 6265 §4.1.1 octet-grammar validation (anti header-splitting). The
        // raw name is deliberately not echoed into the message so a hostile
        // control character cannot leak into logs downstream.
        if (value is null || !HttpCookieGrammar.IsValidName(value))
        {
            message = ErrorMessages.CookieNameInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
