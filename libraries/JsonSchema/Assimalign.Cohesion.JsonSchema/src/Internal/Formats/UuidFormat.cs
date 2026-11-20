using System;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The <c>uuid</c> format: the RFC 4122 §3 string representation, <c>8-4-4-4-12</c> hexadecimal
/// digits in either case.
/// </summary>
/// <remarks>
/// Any version and variant nibble is valid; braces, the <c>urn:uuid:</c> prefix and missing hyphens
/// are not.
/// </remarks>
internal static class UuidFormat
{
    public static bool IsUuid(ReadOnlySpan<char> value, JsonSchemaVersion version)
    {
        if (value.Length != 36)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            bool hyphen = i is 8 or 13 or 18 or 23;
            if (hyphen ? value[i] != '-' : !char.IsAsciiHexDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}
