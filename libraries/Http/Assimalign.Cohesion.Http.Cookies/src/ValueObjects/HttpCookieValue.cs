using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An HTTP cookie value: RFC 6265 &#167; 4.1.1 <c>*cookie-octet</c>, optionally
/// wrapped in a single pair of DQUOTEs. The empty string is a valid value.
/// </summary>
/// <remarks>
/// The value is checked when it is constructed, including through the implicit
/// conversion from <see cref="string"/>, which throws
/// <see cref="System.ArgumentException"/> for <see langword="null"/> or for a
/// control character (including CR/LF), whitespace, DQUOTE, comma, semicolon, or
/// backslash. <see langword="default"/> holds no value; <see cref="HttpCookie"/>
/// reads it as the empty string.
/// </remarks>
public partial struct HttpCookieValue
{
    /// <summary>
    /// Determines whether <paramref name="value"/> is an RFC 6265 &#167; 4.1.1
    /// <c>cookie-value</c>.
    /// </summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The reason the value is refused, or <see langword="null"/> when it is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> contains only cookie-octets, ignoring an optional surrounding DQUOTE pair.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // The raw value is not echoed: it may hold the control character that made it invalid.
        if (value is null || !HttpCookieGrammar.IsValidValue(value))
        {
            message = ErrorMessages.CookieValueInvalid;
            return false;
        }

        message = null;
        return true;
    }
}
