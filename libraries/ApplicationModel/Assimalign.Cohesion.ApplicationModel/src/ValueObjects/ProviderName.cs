namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names a resource provider: a kind of resource, such as <c>Web</c>, <c>Database.Sql</c>, or
/// <c>MessageHub</c>.
/// </summary>
/// <remarks>
/// A provider name is one or more dot-separated segments of ASCII letters and digits, each starting
/// with a letter, at most 128 characters. Names compare case-insensitively, as cloud resource
/// provider namespaces do, so <c>/providers/web</c> and <c>/providers/Web</c> address one provider.
/// </remarks>
public readonly partial struct ProviderName
{
    /// <summary>Validates the provider name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = ErrorMessages.ProviderNameInvalid;
        if (value is null || value.Length is 0 or > 128)
        {
            return false;
        }

        bool segmentStart = true;
        foreach (char character in value)
        {
            if (segmentStart)
            {
                if (!char.IsAsciiLetter(character))
                {
                    return false;
                }

                segmentStart = false;
            }
            else if (character == '.')
            {
                segmentStart = true;
            }
            else if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }
        }

        if (segmentStart)
        {
            // A trailing dot leaves an empty last segment.
            return false;
        }

        message = null;
        return true;
    }
}
