using System;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// A resolved YAML local or global tag spelling, including the nonspecific <c>!</c> tag.
/// </summary>
/// <remarks>
/// YAML 1.2.2 sections 5.6 and 6.9.1 define the URI characters and percent escapes.
/// Local tags start with <c>!</c>; global tags must be absolute URIs.
/// Spelling is preserved and compared ordinally, without URI normalization or escape decoding.
/// Source tag handles are resolved by the parser before this value is created.
/// Use a nullable value for an absent tag; a wrapped <see langword="default"/> is invalid.
/// </remarks>
public partial struct YamlTag
{
    /// <summary>Determines whether a resolved tag is a valid local tag, global URI or nonspecific tag.</summary>
    /// <param name="value">The candidate resolved tag.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> on success.</param>
    /// <returns>Whether the tag has valid YAML characters and escapes, and is local or an absolute URI.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrEmpty(value))
        {
            message = ErrorMessages.TagInvalid;
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (c == '%')
            {
                if (index + 2 >= value.Length || !char.IsAsciiHexDigit(value[index + 1]) || !char.IsAsciiHexDigit(value[index + 2]))
                {
                    message = ErrorMessages.TagInvalid;
                    return false;
                }

                index += 2;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c)
                && c is not ('-' or '#' or ';' or '/' or '?' or ':' or '@' or '&' or '=' or '+' or '$'
                    or ',' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')' or '[' or ']'))
            {
                message = ErrorMessages.TagInvalid;
                return false;
            }
        }

        if (value[0] != '!')
        {
            // System.Uri treats single-letter schemes as DOS drive letters. Expand only the
            // scheme for validation; the value object always retains the original spelling.
            var uri = value.Length > 1 && char.IsAsciiLetter(value[0]) && value[1] == ':'
                ? "yaml-" + value
                : value;
            if (!Uri.IsWellFormedUriString(uri, UriKind.Absolute))
            {
                message = ErrorMessages.TagInvalid;
                return false;
            }
        }

        message = null;
        return true;
    }

    /// <summary>Converts a nullable resolved tag, preserving absence.</summary>
    /// <param name="value">The tag, or <see langword="null"/>.</param>
    /// <returns>The validated tag, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">The tag is invalid.</exception>
    public static implicit operator YamlTag?(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return new YamlTag(value);
    }

    /// <summary>Converts an optional tag to its original spelling.</summary>
    /// <param name="value">The optional tag.</param>
    /// <returns>The tag, or <see langword="null"/> when absent or default.</returns>
    public static implicit operator string?(YamlTag? value) => value?.Value;
}
