namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names a gateway: the root gateway of an application set, or a child gateway it deploys, such
/// as <c>east</c>.
/// </summary>
/// <remarks>
/// A gateway name is an RFC 1035 label, so a platform can name its objects after it: 1 to 63
/// lowercase ASCII letters, digits, or hyphens, starting with a letter and ending with a letter or
/// digit. Gateway names are unique across one application set's whole hierarchy.
/// </remarks>
public readonly partial struct GatewayName
{
    /// <summary>Validates the gateway name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = ErrorMessages.GatewayNameInvalid;
        if (value is null || value.Length is 0 or > 63 || !char.IsAsciiLetterLower(value[0]) || value[^1] == '-')
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character) && character != '-')
            {
                return false;
            }
        }

        message = null;
        return true;
    }
}
