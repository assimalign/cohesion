using System;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The grammar rules of the directory's value objects and of the text a user or a group
/// carries.
/// </summary>
internal static class IdentityDirectoryGrammar
{
    internal const int MaxNameLength = 128;
    internal const int MaxDisplayNameLength = IdentityClientGrammar.MaxDisplayNameLength;
    internal const int MaxDescriptionLength = IdentityClientGrammar.MaxDescriptionLength;
    internal const int MaxPersonNameLength = 64;
    internal const int MaxEmailLength = 254;

    /// <summary>
    /// A user name is ASCII letters, digits, '-', '.', '_', '~', '+', and at most one '@',
    /// starting and ending with a letter or digit. It needs no escaping in a form body or a
    /// query, and the at sign lets it take the shape of an email address.
    /// </summary>
    internal static bool IsUserName(string? value)
    {
        if (value is null || value.Length is 0 or > MaxNameLength)
        {
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(value[0]) || !char.IsAsciiLetterOrDigit(value[^1]))
        {
            return false;
        }

        bool at = false;
        for (int index = 1; index < value.Length - 1; index++)
        {
            char character = value[index];
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~' or '+')
            {
                continue;
            }

            if (character is not '@' || at)
            {
                return false;
            }

            at = true;
        }

        return true;
    }

    /// <summary>
    /// A group name is an identifier: the unreserved characters of RFC 3986 section 2.3,
    /// starting and ending with a letter or digit.
    /// </summary>
    internal static bool IsGroupName(string? value)
    {
        return IdentityClientGrammar.IsIdentifier(value, MaxNameLength);
    }

    /// <summary>
    /// An email address is checked for shape only: 3 to 254 characters with no white space and
    /// no control character, and exactly one '@' with text on both sides. The limit is the
    /// longest path RFC 5321 section 4.5.3.1.3 allows, less its angle brackets. Whether the
    /// address receives mail is what verifying it establishes.
    /// </summary>
    internal static bool IsEmail(string? value)
    {
        if (value is null || value.Length is < 3 or > MaxEmailLength)
        {
            return false;
        }

        int at = -1;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsControl(character) || char.IsWhiteSpace(character) || char.IsSurrogate(character))
            {
                return false;
            }

            if (character is '@')
            {
                if (at >= 0)
                {
                    return false;
                }

                at = index;
            }
        }

        return at > 0 && at < value.Length - 1;
    }

    /// <summary>
    /// Returns the spelling a name is compared and indexed by: lowercase. The grammars admit
    /// ASCII only, so the invariant culture's lowercase is the same on every machine.
    /// </summary>
    internal static string Canonicalize(string name)
    {
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Returns the text of an identifier as a record key spells it: the 36-character lowercase
    /// form with hyphens.
    /// </summary>
    internal static string WriteId(Guid id)
    {
        return id.ToString("D");
    }

    /// <summary>
    /// Reads an identifier from a record key, accepting only the one spelling
    /// <see cref="WriteId"/> writes. Record keys compare ordinally, so a second spelling of one
    /// GUID would be a second record with the same identifier.
    /// </summary>
    internal static bool TryReadId(string? text, out Guid id)
    {
        return Guid.TryParseExact(text, "D", out id) &&
            id != Guid.Empty &&
            string.Equals(id.ToString("D"), text, StringComparison.Ordinal);
    }
}
