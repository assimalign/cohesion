using System;

namespace Assimalign.Cohesion.OpenTelemetry;

/// <summary>An OTLP trace identifier containing exactly 32 ASCII hexadecimal characters.</summary>
/// <remarks>
/// Identity is ordinal and case-insensitive, as permitted by the OTLP JSON encoding specification:
/// <see href="https://opentelemetry.io/docs/specs/otlp/#json-protobuf-encoding"/>.
/// The original spelling is retained; export writes lowercase. <see langword="default"/> holds no identifier.
/// </remarks>
public partial struct OtlpTraceId
{
    /// <summary>Determines whether a value is an OTLP trace identifier.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="message">The rejection reason, or <see langword="null"/> for a valid identifier.</param>
    /// <returns>Whether the value contains exactly 32 ASCII hexadecimal characters.</returns>
    public static partial bool IsValid(string value, out string? message)
    {
        if (value is not null && value.Length == 32)
        {
            foreach (char character in value)
            {
                if (!char.IsAsciiHexDigit(character))
                {
                    message = ErrorMessages.TraceIdInvalid;
                    return false;
                }
            }

            message = null;
            return true;
        }

        message = ErrorMessages.TraceIdInvalid;
        return false;
    }

    /// <summary>Converts an optional string, preserving <see langword="null"/> as no identifier.</summary>
    /// <param name="value">The identifier, or <see langword="null"/>.</param>
    /// <returns>The validated identifier, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentException">The non-null value is not an OTLP trace identifier.</exception>
    public static implicit operator OtlpTraceId?(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return new OtlpTraceId(value);
    }
}
