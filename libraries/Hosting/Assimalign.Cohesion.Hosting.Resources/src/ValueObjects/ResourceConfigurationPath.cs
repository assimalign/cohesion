using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Identifies a manageable configuration section by its configuration path, such as <c>Web:Cors</c>.
/// </summary>
/// <remarks>
/// A path is one or more segments of ASCII letters, digits, dots, hyphens or underscores separated by
/// single colons, at most 256 characters. Paths compare ordinally: the deployment record names a section
/// with exactly the spelling the resource declared.
/// </remarks>
public readonly partial struct ResourceConfigurationPath
{
    /// <summary>Validates the configuration path grammar.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, if any.</param>
    /// <returns>Whether the candidate is valid.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        message = ErrorMessages.ResourceConfigurationPathInvalid;
        if (string.IsNullOrEmpty(value) || value.Length > 256)
        {
            return false;
        }

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
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }

            segmentStart = false;
        }

        if (segmentStart)
        {
            return false;
        }

        message = null;
        return true;
    }
}
