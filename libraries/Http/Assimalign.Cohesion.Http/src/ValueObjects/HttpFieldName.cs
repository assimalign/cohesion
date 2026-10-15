using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A non-empty HTTP field-name token (RFC 9110 §5.1), compared ordinally without
/// case sensitivity. Pseudo-headers are not field names.
/// </summary>
/// <remarks>The original spelling is retained. A required configuration boundary must reject <see langword="default"/>.</remarks>
public partial struct HttpFieldName
{
    /// <summary>Determines whether text is an ordinary HTTP field-name token.</summary>
    /// <param name="value">The candidate name.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> when valid.</param>
    /// <returns>Whether the text is a non-empty ASCII token.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (value is null || !HttpFieldNormalization.IsValidFieldName(value))
        {
            message = ErrorMessages.FieldNameInvalid;
            return false;
        }

        message = null;
        return true;
    }

    /// <summary>Converts an optional name, preserving <see langword="null"/> as absence.</summary>
    /// <param name="value">The name, or <see langword="null"/>.</param>
    /// <returns>The validated name, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">The name is not an HTTP field-name token.</exception>
    public static implicit operator HttpFieldName?(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return new HttpFieldName(value);
    }
}
