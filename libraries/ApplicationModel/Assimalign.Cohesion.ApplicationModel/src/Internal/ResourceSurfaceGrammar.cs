namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// The name grammars of the runtime surface, matching what Hosting.Resources lets a resource declare: section paths,
/// action and tool names, and the platform-qualified keys of plan extensions.
/// </summary>
internal static class ResourceSurfaceGrammar
{
    internal const int MaximumSectionPathLength = 256;
    internal const int MaximumNameLength = 64;

    // One or more segments of ASCII letters, digits, '.', '_' or '-' separated by single colons.
    public static bool IsSectionPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumSectionPathLength)
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

        return !segmentStart;
    }

    // Lowercase ASCII letters and digits in hyphen-separated words, at most 64 characters.
    public static bool IsName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumNameLength || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        char previous = '\0';
        foreach (char character in value)
        {
            if (character == '-')
            {
                if (previous == '-')
                {
                    return false;
                }
            }
            else if (!char.IsAsciiLetterLower(character) && !char.IsAsciiDigit(character))
            {
                return false;
            }

            previous = character;
        }

        return true;
    }

    // '<platform>/<name>', both parts names.
    public static bool IsExtensionKey(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int separator = value.IndexOf('/');
        return separator > 0 &&
            value.IndexOf('/', separator + 1) < 0 &&
            IsName(value[..separator]) &&
            IsName(value[(separator + 1)..]);
    }
}
