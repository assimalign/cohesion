using System;
using System.Buffers.Binary;
using System.Globalization;

namespace Assimalign.Cohesion.Database.Types;

/// <summary>
/// Shared low-level helpers for the order-preserving key component encodings used by
/// <see cref="DatabaseKeyWriter"/> and <see cref="DatabaseKeyReader"/>.
/// </summary>
/// <remarks>
/// Variable-length byte payloads use zero-escaping: <c>0x00</c> becomes
/// <c>0x00 0xFF</c> and the component terminator is <c>0x00 0x00</c>, which keeps
/// byte-wise comparison of encoded components identical to comparison of the raw
/// payloads while making component boundaries unambiguous.
/// </remarks>
internal static class KeyComponentEncoding
{
    internal const byte Escape = 0x00;
    internal const byte EscapedZero = 0xFF;
    internal const byte Terminator = 0x00;

    /// <summary>
    /// Appends an escaped, terminated byte payload.
    /// </summary>
    internal static void WriteEscaped(DatabaseKeyWriter writer, ReadOnlySpan<byte> payload)
    {
        foreach (byte value in payload)
        {
            if (value == Escape)
            {
                writer.WriteByte(Escape);
                writer.WriteByte(EscapedZero);
            }
            else
            {
                writer.WriteByte(value);
            }
        }

        writer.WriteByte(Escape);
        writer.WriteByte(Terminator);
    }

    /// <summary>
    /// Reads an escaped, terminated byte payload, returning the unescaped bytes and
    /// advancing <paramref name="position"/> past the terminator.
    /// </summary>
    internal static byte[] ReadEscaped(ReadOnlySpan<byte> source, ref int position)
    {
        var buffer = new System.Collections.Generic.List<byte>();

        while (true)
        {
            if (position >= source.Length)
            {
                throw new DatabaseTypeException("Malformed key: unterminated variable-length component.");
            }

            byte value = source[position++];

            if (value != Escape)
            {
                buffer.Add(value);
                continue;
            }

            if (position >= source.Length)
            {
                throw new DatabaseTypeException("Malformed key: truncated escape sequence.");
            }

            byte marker = source[position++];

            if (marker == EscapedZero)
            {
                buffer.Add(Escape);
            }
            else if (marker == Terminator)
            {
                return buffer.ToArray();
            }
            else
            {
                throw new DatabaseTypeException($"Malformed key: invalid escape marker 0x{marker:X2}.");
            }
        }
    }

    /// <summary>
    /// Folds IEEE-754 double bits into an unsigned value whose ascending order matches
    /// the numeric total order (negatives reversed, sign bit flipped for positives).
    /// NaN canonicalizes above positive infinity; negative zero orders below zero.
    /// </summary>
    internal static ulong FoldDouble(double value)
    {
        // Canonical POSITIVE quiet NaN — .NET's double.NaN carries the sign bit,
        // which would fold below negative infinity instead of above positive.
        long bits = double.IsNaN(value)
            ? 0x7FF8_0000_0000_0000L
            : BitConverter.DoubleToInt64Bits(value);

        return bits < 0 ? (ulong)~bits : (ulong)bits | 0x8000_0000_0000_0000UL;
    }

    /// <summary>
    /// Reverses <see cref="FoldDouble"/>.
    /// </summary>
    internal static double UnfoldDouble(ulong folded)
    {
        long bits = (folded & 0x8000_0000_0000_0000UL) != 0
            ? (long)(folded & 0x7FFF_FFFF_FFFF_FFFFUL)
            : ~(long)folded;
        return BitConverter.Int64BitsToDouble(bits);
    }

    /// <summary>
    /// Folds IEEE-754 single bits (see <see cref="FoldDouble"/>).
    /// </summary>
    internal static uint FoldSingle(float value)
    {
        // Canonical positive quiet NaN (see FoldDouble).
        int bits = float.IsNaN(value)
            ? 0x7FC0_0000
            : BitConverter.SingleToInt32Bits(value);

        return bits < 0 ? (uint)~bits : (uint)bits | 0x8000_0000u;
    }

    /// <summary>
    /// Reverses <see cref="FoldSingle"/>.
    /// </summary>
    internal static float UnfoldSingle(uint folded)
    {
        int bits = (folded & 0x8000_0000u) != 0
            ? (int)(folded & 0x7FFF_FFFFu)
            : ~(int)folded;
        return BitConverter.Int32BitsToSingle(bits);
    }

    private const byte decimalNegative = 0;
    private const byte decimalZero = 1;
    private const byte decimalPositive = 2;
    private const int decimalExponentBias = 64;

    /// <summary>
    /// Writes an order-preserving decimal encoding: a sign byte, then for non-zero
    /// values a biased base-10 exponent followed by the normalized significant digits
    /// (each digit stored as <c>digit + 1</c>) and a zero terminator; negative values
    /// store the ones' complement of the exponent/digit section so larger magnitudes
    /// order first.
    /// </summary>
    internal static void WriteDecimal(DatabaseKeyWriter writer, decimal value)
    {
        if (value == 0m)
        {
            writer.WriteByte(decimalZero);
            return;
        }

        bool negative = value < 0m;
        writer.WriteByte(negative ? decimalNegative : decimalPositive);

        var (digits, exponent) = Normalize(Math.Abs(value));

        byte exponentByte = checked((byte)(exponent + decimalExponentBias));
        writer.WriteByte(negative ? (byte)~exponentByte : exponentByte);

        foreach (char digit in digits)
        {
            byte encoded = (byte)(digit - '0' + 1);
            writer.WriteByte(negative ? (byte)~encoded : encoded);
        }

        writer.WriteByte(negative ? (byte)0xFF : (byte)0x00);
    }

    /// <summary>
    /// Reads a decimal written by <see cref="WriteDecimal"/>.
    /// </summary>
    internal static decimal ReadDecimal(ReadOnlySpan<byte> source, ref int position)
    {
        if (position >= source.Length)
        {
            throw new DatabaseTypeException("Malformed key: truncated decimal component.");
        }

        byte sign = source[position++];

        if (sign == decimalZero)
        {
            return 0m;
        }

        if (sign is not (decimalNegative or decimalPositive))
        {
            throw new DatabaseTypeException($"Malformed key: invalid decimal sign byte 0x{sign:X2}.");
        }

        bool negative = sign == decimalNegative;

        if (position >= source.Length)
        {
            throw new DatabaseTypeException("Malformed key: truncated decimal exponent.");
        }

        byte exponentByte = source[position++];
        int exponent = (negative ? (byte)~exponentByte : exponentByte) - decimalExponentBias;

        var digits = new System.Text.StringBuilder();

        while (true)
        {
            if (position >= source.Length)
            {
                throw new DatabaseTypeException("Malformed key: unterminated decimal digits.");
            }

            byte raw = source[position++];
            byte encoded = negative ? (byte)~raw : raw;

            if (encoded == 0x00)
            {
                break;
            }

            if (encoded is < 1 or > 10)
            {
                throw new DatabaseTypeException($"Malformed key: invalid decimal digit byte 0x{raw:X2}.");
            }

            digits.Append((char)('0' + encoded - 1));
        }

        if (digits.Length == 0)
        {
            throw new DatabaseTypeException("Malformed key: decimal component has no digits.");
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"{digits[0]}.{(digits.Length > 1 ? digits.ToString(1, digits.Length - 1) : "0")}E{exponent}");
        decimal magnitude = decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return negative ? -magnitude : magnitude;
    }

    /// <summary>
    /// Normalizes a positive decimal into significant digits (no leading or trailing
    /// zeros) and a base-10 exponent such that the value equals
    /// <c>0.d1d2... × 10^(exponent + 1)</c> with <c>d1 ≠ 0</c>.
    /// </summary>
    private static (string Digits, int Exponent) Normalize(decimal magnitude)
    {
        string text = magnitude.ToString(CultureInfo.InvariantCulture);

        int dot = text.IndexOf('.');
        string integer = dot < 0 ? text : text[..dot];
        string fraction = dot < 0 ? string.Empty : text[(dot + 1)..];

        string all = integer.TrimStart('0');
        int exponent;

        if (all.Length > 0)
        {
            exponent = all.Length - 1;
            all += fraction;
        }
        else
        {
            int leadingZeros = 0;
            while (leadingZeros < fraction.Length && fraction[leadingZeros] == '0')
            {
                leadingZeros++;
            }

            exponent = -leadingZeros - 1;
            all = fraction[leadingZeros..];
        }

        all = all.TrimEnd('0');
        return (all, exponent);
    }
}
