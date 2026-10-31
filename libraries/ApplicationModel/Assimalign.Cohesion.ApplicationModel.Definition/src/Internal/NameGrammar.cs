namespace Assimalign.Cohesion.ApplicationModel.Internal;

// The grammar output and extension names share with the contracts' platform names: lowercase ASCII
// letters and digits in hyphen-separated words.
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
