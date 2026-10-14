using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// An HTTP field value (RFC 9110 §5.5), preserving case with ordinal identity.
/// Refuses every CTL except internal HTAB and refuses leading or trailing SP or HTAB.
/// </summary>
/// <remarks>
/// Empty text is valid; <see langword="default"/> represents no value. CTL means
/// %x00-1F / %x7F (RFC 5234 §B.1); Unicode control characters are also refused.
/// Printable obs-text is retained. Character encoding remains
/// the transport's responsibility, as for <see cref="HttpFieldNormalization"/>.
/// </remarks>
public partial struct HttpFieldValue
{
    /// <summary>Determines whether text can be used as one HTTP field value.</summary>
    /// <param name="value">The candidate value.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> when valid.</param>
    /// <returns>Whether the text passes the field-content and control-character rules.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (value is null
            || !HttpFieldNormalization.IsValidFieldValue(value)
            || HttpFieldNormalization.IndexOfInvalidControlCharacter(value) >= 0)
        {
            message = ErrorMessages.FieldValueInvalid;
            return false;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character) && character != '\t')
            {
                message = ErrorMessages.FieldValueInvalid;
                return false;
            }
        }

        message = null;
        return true;
    }

    /// <summary>Converts an optional value, preserving <see langword="null"/> as absence.</summary>
    /// <param name="value">The value, or <see langword="null"/>.</param>
    /// <returns>The validated value, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">The text is not valid field content.</exception>
    public static implicit operator HttpFieldValue?(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return new HttpFieldValue(value);
    }
}
