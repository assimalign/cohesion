namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>The shared grammar of action and tool names: lowercase hyphen-separated ASCII words.</summary>
internal static class ResourceSurfaceNameGrammar
{
    internal const int MaximumLength = 64;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength || value[0] == '-' || value[^1] == '-')
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
}
