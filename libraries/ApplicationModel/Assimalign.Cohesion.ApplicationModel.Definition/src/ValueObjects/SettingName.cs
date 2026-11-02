namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names one setting of a resource: a configuration key its instances read, such as
/// <c>ConfigurationStore:Endpoint</c>.
/// </summary>
/// <remarks>
/// A setting name is colon-separated segments of ASCII letters, digits, underscores, hyphens, and
/// periods, at most 256 characters. Like configuration keys, names compare case-insensitively.
/// </remarks>
public readonly partial struct SettingName
{
    /// <summary>Validates the setting name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = ErrorMessages.SettingNameInvalid;
        if (value is null || value.Length is 0 or > 256)
        {
            return false;
        }

        // A colon separates segments, so it may neither lead, trail, nor repeat.
        bool segmentStart = true;
        foreach (char character in value)
        {
            if (character == ':')
            {
                if (segmentStart)
                {
                    return false;
                }

                segmentStart = true;
            }
            else if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            {
                segmentStart = false;
            }
            else
            {
                return false;
            }
        }

        if (segmentStart)
        {
            return false;
        }

        message = null;
        return true;
    }
}
