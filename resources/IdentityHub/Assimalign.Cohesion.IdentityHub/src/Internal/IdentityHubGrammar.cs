namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The grammar rules shared by the store seam's value objects.
/// </summary>
internal static class IdentityHubGrammar
{
    internal const int MaxNameLength = 64;
    internal const int MaxKeyLength = 512;
    internal const int MaxVersionLength = 128;

    /// <summary>
    /// A name is segments of lowercase ASCII letters and digits separated by one '.' or '-',
    /// starting with a letter.
    /// </summary>
    internal static bool IsName(string? value)
    {
        if (value is null || value.Length is 0 or > MaxNameLength)
        {
            return false;
        }

        if (value[0] is < 'a' or > 'z')
        {
            return false;
        }

        bool separator = false;
        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                separator = false;
                continue;
            }

            if (character is not ('.' or '-') || separator)
            {
                return false;
            }

            separator = true;
        }

        return !separator;
    }

    /// <summary>
    /// A key holds no control character and is well-formed UTF-16, so that every distinct key
    /// has a distinct UTF-8 form.
    /// </summary>
    internal static bool IsKey(string? value)
    {
        if (value is null || value.Length is 0 or > MaxKeyLength)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsControl(character))
            {
                return false;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A version is visible ASCII with no space, so that it can travel in a header or a URL.
    /// </summary>
    internal static bool IsVersion(string? value)
    {
        if (value is null || value.Length is 0 or > MaxVersionLength)
        {
            return false;
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
