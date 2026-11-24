using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// An exact decimal JSON number: <c>(-1)^negative × significand × 10^exponent</c>, normalized so the
/// significand has no trailing zeros and zero is unsigned.
/// </summary>
/// <remarks>
/// JSON Schema compares numbers by mathematical value, so <c>1</c>, <c>1.0</c> and <c>10e-1</c> are
/// equal, <c>1.0</c> is an integer, and <c>multipleOf</c> is exact for any precision. Comparisons
/// look at magnitude (digit count plus exponent) before they align significands, so a value such as
/// <c>1e999999999</c> never materializes its power of ten.
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
internal readonly struct JsonNumber : IEquatable<JsonNumber>, IComparable<JsonNumber>
{
    // Exponents saturate here: far beyond any meaningful magnitude, far from overflow.
    private const long exponentLimit = long.MaxValue / 4;

    private JsonNumber(bool negative, BigInteger significand, long exponent, int digits)
    {
        Negative = negative;
        Significand = significand;
        Exponent = exponent;
        Digits = digits;
    }

    public bool Negative { get; }

    public BigInteger Significand { get; }

    public long Exponent { get; }

    /// <summary>Gets the number of decimal digits in <see cref="Significand"/> (zero for zero).</summary>
    public int Digits { get; }

    public bool IsZero => Digits == 0;

    public bool IsInteger => Exponent >= 0;

    public static JsonNumber Zero => default;

    /// <summary>Reads the exact value of a number element from its raw JSON text.</summary>
    public static JsonNumber FromElement(JsonElement element)
    {
        Debug.Assert(element.ValueKind == JsonValueKind.Number);
        ReadOnlySpan<byte> raw = JsonMarshal.GetRawUtf8Value(element);
        if (!TryParse(raw, out JsonNumber number))
        {
            throw new FormatException();
        }

        return number;
    }

    /// <summary>Creates the exact value of an integer.</summary>
    public static JsonNumber FromInt64(long value)
    {
        if (value == 0)
        {
            return default;
        }

        bool negative = value < 0;
        BigInteger significand = BigInteger.Abs(new BigInteger(value));
        long exponent = 0;
        while (significand % 10 == 0)
        {
            significand /= 10;
            exponent++;
        }

        return new JsonNumber(negative, significand, exponent, CountDigits(significand));
    }

    /// <summary>
    /// Parses JSON number text (RFC 8259 §6), saturating exponents rather than overflowing.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> text, out JsonNumber number)
    {
        number = default;
        int i = 0;
        bool negative = false;
        if (i < text.Length && text[i] == '-')
        {
            negative = true;
            i++;
        }

        int integerStart = i;
        while (i < text.Length && IsDigit(text[i]))
        {
            i++;
        }

        int integerEnd = i;
        if (integerEnd == integerStart)
        {
            return false;
        }

        int fractionStart = i;
        int fractionEnd = i;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            fractionStart = i;
            while (i < text.Length && IsDigit(text[i]))
            {
                i++;
            }

            fractionEnd = i;
            if (fractionEnd == fractionStart)
            {
                return false;
            }
        }

        long exponent = 0;
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            bool exponentNegative = false;
            if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            {
                exponentNegative = text[i] == '-';
                i++;
            }

            int exponentStart = i;
            while (i < text.Length && IsDigit(text[i]))
            {
                if (exponent < exponentLimit)
                {
                    exponent = (exponent * 10) + (text[i] - '0');
                }

                i++;
            }

            if (i == exponentStart)
            {
                return false;
            }

            exponent = Math.Min(exponent, exponentLimit);
            if (exponentNegative)
            {
                exponent = -exponent;
            }
        }

        if (i != text.Length)
        {
            return false;
        }

        int fractionLength = fractionEnd - fractionStart;
        int totalDigits = (integerEnd - integerStart) + fractionLength;
        byte[]? rented = null;
        Span<byte> digits = totalDigits <= 256 ? stackalloc byte[totalDigits] : (rented = ArrayPool<byte>.Shared.Rent(totalDigits)).AsSpan(0, totalDigits);
        try
        {
            text.Slice(integerStart, integerEnd - integerStart).CopyTo(digits);
            text.Slice(fractionStart, fractionLength).CopyTo(digits.Slice(integerEnd - integerStart));
            int first = 0;
            while (first < digits.Length && digits[first] == '0')
            {
                first++;
            }

            if (first == digits.Length)
            {
                number = default;
                return true;
            }

            int last = digits.Length - 1;
            int trailing = 0;
            while (digits[last] == '0')
            {
                last--;
                trailing++;
            }

            Span<byte> significandDigits = digits.Slice(first, last - first + 1);
            long scaled = SaturatingAdd(exponent, trailing - (long)fractionLength);
            number = new JsonNumber(negative, ParseDigits(significandDigits), scaled, significandDigits.Length);
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public int CompareTo(JsonNumber other)
    {
        if (IsZero || other.IsZero)
        {
            int left = IsZero ? 0 : (Negative ? -1 : 1);
            int right = other.IsZero ? 0 : (other.Negative ? -1 : 1);
            return left.CompareTo(right);
        }

        if (Negative != other.Negative)
        {
            return Negative ? -1 : 1;
        }

        int magnitude = CompareMagnitude(this, other);
        return Negative ? -magnitude : magnitude;
    }

    /// <summary>
    /// Determines whether this value is an integer multiple of a positive divisor, exactly.
    /// </summary>
    public bool IsMultipleOf(JsonNumber divisor)
    {
        Debug.Assert(!divisor.IsZero && !divisor.Negative);
        if (IsZero)
        {
            return true;
        }

        long shift = Exponent - divisor.Exponent;
        if (shift >= 0)
        {
            // significand * 10^shift ≡ 0 (mod divisor.significand)
            BigInteger power = BigInteger.ModPow(10, shift, divisor.Significand);
            return (Significand % divisor.Significand * power % divisor.Significand).IsZero;
        }

        // The quotient is significand / (divisor.significand * 10^-shift), an integer only if 10^-shift
        // divides the significand. A normalized significand has no trailing zeros, so it never does.
        return false;
    }

    public bool Equals(JsonNumber other) =>
        Negative == other.Negative && Exponent == other.Exponent && Significand.Equals(other.Significand);

    public override bool Equals(object? obj) => obj is JsonNumber other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Negative, Exponent, Significand);

    public override string ToString()
    {
        if (IsZero)
        {
            return "0";
        }

        string digits = Significand.ToString(CultureInfo.InvariantCulture);
        string sign = Negative ? "-" : string.Empty;
        if (Exponent >= 0 && Exponent <= 21)
        {
            return sign + digits + new string('0', (int)Exponent);
        }

        if (Exponent < 0 && -Exponent < digits.Length)
        {
            int point = digits.Length + (int)Exponent;
            return sign + digits.Substring(0, point) + "." + digits.Substring(point);
        }

        if (Exponent < 0 && -Exponent - digits.Length <= 6)
        {
            return sign + "0." + new string('0', (int)(-Exponent - digits.Length)) + digits;
        }

        long scientific = Exponent + digits.Length - 1;
        string mantissa = digits.Length == 1 ? digits : digits.Substring(0, 1) + "." + digits.Substring(1);
        return sign + mantissa + "E" + (scientific >= 0 ? "+" : string.Empty) + scientific.ToString(CultureInfo.InvariantCulture);
    }

    private static int CompareMagnitude(JsonNumber left, JsonNumber right)
    {
        long leftOrder = left.Digits + left.Exponent;
        long rightOrder = right.Digits + right.Exponent;
        if (leftOrder != rightOrder)
        {
            return leftOrder < rightOrder ? -1 : 1;
        }

        // Same leading-digit position: the exponents differ by at most the digit counts, so aligning
        // the significands is cheap.
        long common = Math.Min(left.Exponent, right.Exponent);
        BigInteger l = left.Significand * BigInteger.Pow(10, (int)(left.Exponent - common));
        BigInteger r = right.Significand * BigInteger.Pow(10, (int)(right.Exponent - common));
        return l.CompareTo(r);
    }

    private static BigInteger ParseDigits(ReadOnlySpan<byte> digits)
    {
        if (digits.Length <= 18)
        {
            ulong value = 0;
            foreach (byte b in digits)
            {
                value = (value * 10) + (ulong)(b - '0');
            }

            return new BigInteger(value);
        }

        char[] rented = ArrayPool<char>.Shared.Rent(digits.Length);
        try
        {
            for (int i = 0; i < digits.Length; i++)
            {
                rented[i] = (char)digits[i];
            }

            return BigInteger.Parse(rented.AsSpan(0, digits.Length), NumberStyles.None, CultureInfo.InvariantCulture);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static int CountDigits(BigInteger value) =>
        value.IsZero ? 0 : value.ToString(CultureInfo.InvariantCulture).Length;

    private static long SaturatingAdd(long left, long right)
    {
        long sum = left + right;
        return Math.Clamp(sum, -exponentLimit, exponentLimit);
    }

    private static bool IsDigit(byte b) => b >= '0' && b <= '9';
}
