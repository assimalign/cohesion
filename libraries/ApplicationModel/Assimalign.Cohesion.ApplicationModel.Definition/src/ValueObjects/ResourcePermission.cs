namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Names a permission one resource needs on another, such as <c>data.read</c> or
/// <c>messages.send</c>.
/// </summary>
/// <remarks>
/// <para>
/// A permission is one or more dot-separated segments, each lowercase ASCII letters and digits in
/// hyphen-separated words, at most 128 characters. The target resource's provider defines which
/// permissions exist; the gateway registers the ones a dependency names with the tenant's identity
/// provider, which issues credentials for them when the dependent resource runs.
/// </para>
/// </remarks>
public readonly partial struct ResourcePermission
{
    /// <summary>Validates the permission grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = ErrorMessages.ResourcePermissionInvalid;
        if (value is null || value.Length is 0 or > 128)
        {
            return false;
        }

        // Each word is a non-empty run of letters and digits; a hyphen joins words in a segment
        // and a dot joins segments, so neither may lead, trail, or repeat.
        bool wordStart = true;
        foreach (char character in value)
        {
            if (character is '-' or '.')
            {
                if (wordStart)
                {
                    return false;
                }

                wordStart = true;
            }
            else if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
            {
                wordStart = false;
            }
            else
            {
                return false;
            }
        }

        if (wordStart)
        {
            return false;
        }

        message = null;
        return true;
    }
}
