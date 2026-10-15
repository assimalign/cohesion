using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// An HTTP authentication scheme, using the token grammar of RFC 9110 sections 5.6.2 and 11.1.
/// </summary>
/// <remarks>
/// Schemes compare ignoring ASCII letter case, as required by RFC 9110 section 11.1.
/// The supplied spelling is preserved. A default value has no scheme.
/// </remarks>
public partial struct ApplicationCredentialScheme
{
    /// <summary>
    /// Determines whether a string is a nonempty HTTP authentication token.
    /// </summary>
    /// <param name="value">The candidate scheme.</param>
    /// <param name="message">The reason for rejection, or null when valid.</param>
    /// <returns>True when the scheme follows the HTTP token grammar.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrEmpty(value))
        {
            message = ErrorMessages.ApplicationCredentialSchemeInvalid;
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not ('!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                message = ErrorMessages.ApplicationCredentialSchemeInvalid;
                return false;
            }
        }

        message = null;
        return true;
    }
}
