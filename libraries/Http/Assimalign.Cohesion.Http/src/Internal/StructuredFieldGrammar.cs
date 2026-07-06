using System;
using System.Text;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Shared RFC 9651 lexical primitives: character-class predicates, ABNF validators, and
/// the low-level canonical serializers for the individual bare item kinds. Both the
/// span-based parser and the field serializers build on this so the syntax rules live in
/// exactly one place.
/// </summary>
internal static class StructuredFieldGrammar
{
    // ------------------------------------------------------------------
    // Character classes (RFC 9651 §3.3, RFC 9110 tchar)
    // ------------------------------------------------------------------

    internal static bool IsDigit(char c) => c is >= '0' and <= '9';

    internal static bool IsLowerAlpha(char c) => c is >= 'a' and <= 'z';

    internal static bool IsAlpha(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    internal static bool IsLowerHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');

    /// <summary>RFC 9110 <c>tchar</c>.</summary>
    internal static bool IsTChar(char c)
        => IsDigit(c) || IsAlpha(c) || c switch
        {
            '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or
            '.' or '^' or '_' or '`' or '|' or '~' => true,
            _ => false,
        };

    /// <summary>A character permitted after the first character of an <c>sf-token</c> (RFC 9651 §3.3.4).</summary>
    internal static bool IsTokenTailChar(char c) => IsTChar(c) || c == ':' || c == '/';

    /// <summary>The first character of a <c>key</c> (RFC 9651 §3.1.2).</summary>
    internal static bool IsKeyStartChar(char c) => IsLowerAlpha(c) || c == '*';

    /// <summary>A non-leading character of a <c>key</c> (RFC 9651 §3.1.2).</summary>
    internal static bool IsKeyTailChar(char c)
        => IsLowerAlpha(c) || IsDigit(c) || c is '_' or '-' or '.' or '*';

    // ------------------------------------------------------------------
    // Validators
    // ------------------------------------------------------------------

    /// <summary>Determines whether <paramref name="value"/> matches the <c>sf-token</c> grammar (RFC 9651 §3.3.4).</summary>
    internal static bool IsValidToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }
        char first = value[0];
        if (!IsAlpha(first) && first != '*')
        {
            return false;
        }
        for (int i = 1; i < value.Length; i++)
        {
            if (!IsTokenTailChar(value[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Determines whether <paramref name="value"/> matches the <c>key</c> grammar (RFC 9651 §3.1.2).</summary>
    internal static bool IsValidKey(string value)
    {
        if (value.Length == 0 || !IsKeyStartChar(value[0]))
        {
            return false;
        }
        for (int i = 1; i < value.Length; i++)
        {
            if (!IsKeyTailChar(value[i]))
            {
                return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------
    // Serializers (RFC 9651 §4.1)
    // ------------------------------------------------------------------

    /// <summary>Serializes a key (RFC 9651 §4.1.1.3).</summary>
    internal static void WriteKey(StringBuilder builder, string key)
    {
        if (!IsValidKey(key))
        {
            throw new HttpInvalidStructuredFieldException($"'{key}' is not a valid RFC 9651 key.");
        }
        builder.Append(key);
    }

    /// <summary>Serializes a Decimal (RFC 9651 §4.1.5).</summary>
    internal static void WriteDecimal(StringBuilder builder, decimal value)
    {
        // Round to three fractional digits using round-half-to-even.
        decimal rounded = Math.Round(value, 3, MidpointRounding.ToEven);
        bool negative = rounded < 0m;
        decimal abs = Math.Abs(rounded);

        decimal integerDecimal = decimal.Truncate(abs);
        if (integerDecimal >= 1_000_000_000_000m)
        {
            throw new HttpInvalidStructuredFieldException("Decimal integer component exceeds the RFC 9651 limit of 12 digits.");
        }

        long integerPart = (long)integerDecimal;
        // The fractional component now has at most three digits, so scaling by 1000 is exact.
        int thousandths = (int)decimal.Truncate((abs - integerDecimal) * 1000m);

        if (negative)
        {
            builder.Append('-');
        }
        builder.Append(integerPart);
        builder.Append('.');

        if (thousandths == 0)
        {
            builder.Append('0');
            return;
        }

        int d1 = thousandths / 100;
        int d2 = (thousandths / 10) % 10;
        int d3 = thousandths % 10;
        if (d3 != 0)
        {
            builder.Append((char)('0' + d1)).Append((char)('0' + d2)).Append((char)('0' + d3));
        }
        else if (d2 != 0)
        {
            builder.Append((char)('0' + d1)).Append((char)('0' + d2));
        }
        else
        {
            builder.Append((char)('0' + d1));
        }
    }

    /// <summary>Serializes a String (RFC 9651 §4.1.6).</summary>
    internal static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (char c in value)
        {
            if (c < ' ' || c > '~')
            {
                throw new HttpInvalidStructuredFieldException("String contains a character outside the printable ASCII range (%x20-7E).");
            }
            if (c == '"' || c == '\\')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        builder.Append('"');
    }

    /// <summary>Serializes a Token (RFC 9651 §4.1.7).</summary>
    internal static void WriteToken(StringBuilder builder, string value)
    {
        if (!IsValidToken(value))
        {
            throw new HttpInvalidStructuredFieldException($"'{value}' is not a valid RFC 9651 sf-token.");
        }
        builder.Append(value);
    }

    /// <summary>Serializes a Byte Sequence (RFC 9651 §4.1.8).</summary>
    internal static void WriteByteSequence(StringBuilder builder, byte[] value)
    {
        builder.Append(':');
        builder.Append(Convert.ToBase64String(value));
        builder.Append(':');
    }

    /// <summary>Serializes a Display String (RFC 9651 §4.1.11).</summary>
    internal static void WriteDisplayString(StringBuilder builder, string value)
    {
        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException)
        {
            throw new HttpInvalidStructuredFieldException("Display String contains an unpaired surrogate and cannot be encoded as UTF-8.");
        }

        builder.Append('%').Append('"');
        foreach (byte b in utf8)
        {
            if (b == (byte)'%' || b == (byte)'"' || b < 0x20 || b > 0x7E)
            {
                builder.Append('%');
                builder.Append(ToLowerHex(b >> 4));
                builder.Append(ToLowerHex(b & 0xF));
            }
            else
            {
                builder.Append((char)b);
            }
        }
        builder.Append('"');
    }

    /// <summary>A strict UTF-8 codec that throws rather than substituting replacement characters.</summary>
    internal static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static char ToLowerHex(int nibble) => (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));
}
