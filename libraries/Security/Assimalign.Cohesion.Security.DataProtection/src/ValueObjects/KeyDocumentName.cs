using System;
using System.Globalization;
using System.IO;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// A non-empty key-document name containing no invalid file-name character for the current
/// platform, no <c>..</c> substring, and no rooted path.
/// </summary>
/// <remarks>
/// This preserves the file-system repository's naming rule. Separator and rooted-path checks
/// follow <see cref="Path.GetInvalidFileNameChars"/> and <see cref="Path.IsPathRooted(string)"/>:
/// a backslash is refused on Windows but remains a legal file-name character on Unix.
/// Names retain the original string's ordinal, case-sensitive identity; file-system case
/// behavior does not redefine document identity. <see langword="default"/> holds no name
/// and is refused by <see cref="KeyDocument"/>.
/// </remarks>
public readonly partial struct KeyDocumentName
{
    /// <summary>
    /// Determines whether a value satisfies the key repository's platform-specific naming rule.
    /// </summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> for a valid name.</param>
    /// <returns><see langword="true"/> when the name is valid for the current platform.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (string.IsNullOrEmpty(value))
        {
            message = ErrorMessages.KeyDocumentNameRequired;
            return false;
        }

        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(value))
        {
            message = string.Format(CultureInfo.CurrentCulture, ErrorMessages.KeyDocumentNameInvalid, value);
            return false;
        }

        message = null;
        return true;
    }
}
