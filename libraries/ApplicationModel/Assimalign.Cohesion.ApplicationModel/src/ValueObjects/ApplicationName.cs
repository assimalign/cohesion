using System;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Identifies a Cohesion application and its ownership boundary using a lowercase RFC 1123 label.
/// </summary>
public partial struct ApplicationName
{
    /// <summary>
    /// Determines whether a name contains 1-63 lowercase ASCII letters, digits, or hyphens,
    /// beginning and ending with a letter or digit.
    /// </summary>
    /// <param name="value">The candidate application name.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> for a valid name.</param>
    /// <returns>Whether the name satisfies the application's RFC 1123 label rule.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        // Application identities preserve lowercase spelling and use ordinal comparison.
        if (string.IsNullOrEmpty(value) || value.Length > 63 ||
            !IsAsciiLetterOrDigit(value[0]) || !IsAsciiLetterOrDigit(value[^1]))
        {
            message = ErrorMessages.ApplicationNameInvalid;
            return false;
        }

        foreach (char character in value)
        {
            if (!IsAsciiLetterOrDigit(character) && character != '-')
            {
                message = ErrorMessages.ApplicationNameInvalid;
                return false;
            }
        }

        message = null;
        return true;
    }

    /// <summary>
    /// Creates an application name from its textual representation.
    /// </summary>
    /// <param name="value">The application name.</param>
    /// <returns>The validated application name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid application name.</exception>
    public static ApplicationName Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
