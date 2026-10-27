namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Names an action a resource exposes, such as <c>log-level</c> or <c>flush-cache</c>.
/// </summary>
/// <remarks>
/// An action name is lowercase ASCII letters and digits in hyphen-separated words, at most 64
/// characters, so it can stand as one segment of any transport's route.
/// </remarks>
public readonly partial struct ResourceActionName
{
    /// <summary>Validates the action name grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = ErrorMessages.ResourceActionNameInvalid;
        if (value is null || value.Length is 0 or > 64)
        {
            return false;
        }

        bool wordStart = true;
        foreach (char character in value)
        {
            if (character == '-')
            {
                if (wordStart)
                {
                    // A leading or doubled hyphen leaves an empty word.
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
            // A trailing hyphen leaves an empty last word.
            return false;
        }

        message = null;
        return true;
    }
}
