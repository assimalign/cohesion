// Shared source: compiled into the root, Protocol, and Definition, each of which declares the
// Assimalign.Cohesion.ApplicationModel root namespace, so the linked type keeps its owning namespace.
namespace Assimalign.Cohesion.ApplicationModel.Internal;

// The grammar of every hyphenated name in the application model: the root's platform names,
// Protocol's action names, and Definition's output and extension names. Lowercase ASCII letters and
// digits in hyphen-separated words.
internal static class NameGrammar
{
    public static bool IsHyphenatedWords(string value, int maximumLength)
    {
        if (value is null || value.Length == 0 || value.Length > maximumLength)
        {
            return false;
        }

        // Each word is a non-empty run of letters and digits; a hyphen joins words, so it may
        // neither lead, trail, nor repeat.
        bool wordStart = true;
        foreach (char character in value)
        {
            if (character == '-')
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

        return !wordStart;
    }
}
