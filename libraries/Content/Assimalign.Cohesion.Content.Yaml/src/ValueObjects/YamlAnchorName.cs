using System;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// A YAML 1.2 anchor name without its leading <c>&amp;</c> or <c>*</c> indicator.
/// </summary>
/// <remarks>
/// Implements <c>ns-anchor-name</c> from YAML 1.2.2 sections 5.1, 5.4, 5.5 and 6.9.2.
/// Names retain their spelling and compare ordinally: aliases identify names exactly.
/// Use a nullable value for an absent anchor; a wrapped <see langword="default"/> is invalid.
/// </remarks>
public partial struct YamlAnchorName
{
    /// <summary>Determines whether a value is a nonempty YAML <c>ns-anchor-name</c>.</summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> on success.</param>
    /// <returns>Whether the name contains only YAML nonspace characters other than flow indicators.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrEmpty(value))
        {
            message = ErrorMessages.AnchorNameInvalid;
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (c is ',' or '[' or ']' or '{' or '}' or '\uFEFF')
            {
                message = ErrorMessages.AnchorNameInvalid;
                return false;
            }

            // YAML's ns-char is not the complement of Unicode whitespace. NEL,
            // NBSP and Unicode line/paragraph separators are ordinary characters.
            if (c is >= '\u0021' and <= '\u007E' or '\u0085'
                or >= '\u00A0' and <= '\uD7FF' or >= '\uE000' and <= '\uFFFD')
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }

            message = ErrorMessages.AnchorNameInvalid;
            return false;
        }

        message = null;
        return true;
    }

    /// <summary>Converts a nullable name, preserving absence.</summary>
    /// <param name="value">The name, or <see langword="null"/>.</param>
    /// <returns>The validated name, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">The name is invalid.</exception>
    public static implicit operator YamlAnchorName?(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return new YamlAnchorName(value);
    }

    /// <summary>Converts an optional name to its original spelling.</summary>
    /// <param name="value">The optional name.</param>
    /// <returns>The name, or <see langword="null"/> when absent or default.</returns>
    public static implicit operator string?(YamlAnchorName? value) => value?.Value;
}
