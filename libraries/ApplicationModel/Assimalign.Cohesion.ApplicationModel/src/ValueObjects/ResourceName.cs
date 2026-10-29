namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names a resource: the logical service every instance of it shares, such as <c>orders-api</c>.
/// </summary>
/// <remarks>
/// A resource name is an RFC 1035 label, the form Kubernetes requires of a Service name: 1 to 63
/// lowercase ASCII letters, digits, or hyphens, starting with a letter and ending with a letter or
/// digit. The grammar admits no uppercase letters, so names compare ordinally.
/// </remarks>
public readonly partial struct ResourceName
{
    /// <summary>Validates the resource name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = ErrorMessages.ResourceNameInvalid;
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
