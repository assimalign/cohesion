using System;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// Punycode (RFC 3492), the encoding between an A-label's ASCII form and its U-label's code points.
/// </summary>
/// <remarks>
/// Decoding is strict: it fails on a character that is not a digit, on overflow, on a decoded basic or
/// surrogate code point, and on a result longer than the caller's buffer. Callers detect
/// non-canonical encodings by encoding the result again.
/// </remarks>
internal static class Punycode
{
    private const int @base = 36;
    private const int tmin = 1;
    private const int tmax = 26;
    private const int skew = 38;
    private const int damp = 700;
    private const int initialBias = 72;
    private const int initialN = 0x80;

    /// <summary>
    /// Decodes Punycode into code points.
    /// </summary>
    /// <returns>The number of code points written, or -1 when the input is not valid Punycode or does not fit.</returns>
    public static int Decode(ReadOnlySpan<char> input, Span<int> output)
    {
        int basicCount = input.LastIndexOf('-');
        if (basicCount < 0)
        {
            basicCount = 0;
        }

        if (basicCount > output.Length)
        {
            return -1;
        }

        for (int j = 0; j < basicCount; j++)
        {
            char c = input[j];
            if (c >= 0x80)
            {
                return -1;
            }

            output[j] = c;
        }

        int length = basicCount;
        int n = initialN;
        int bias = initialBias;
        int i = 0;
        for (int position = basicCount > 0 ? basicCount + 1 : 0; position < input.Length;)
        {
            int oldI = i;
            int w = 1;
            for (int k = @base; ; k += @base)
            {
                if (position >= input.Length)
                {
                    return -1;
                }

                int digit = DecodeDigit(input[position++]);
                if (digit >= @base || digit > (int.MaxValue - i) / w)
                {
                    return -1;
                }

                i += digit * w;
                int t = Threshold(k, bias);
                if (digit < t)
                {
                    break;
                }

                if (w > int.MaxValue / (@base - t))
                {
                    return -1;
                }

                w *= @base - t;
            }

            bias = Adapt(i - oldI, length + 1, oldI == 0);
            if (i / (length + 1) > int.MaxValue - n)
            {
                return -1;
            }

            n += i / (length + 1);
            i %= length + 1;
            if (n < 0x80 || n > 0x10FFFF || n is >= 0xD800 and <= 0xDFFF || length >= output.Length)
            {
                return -1;
            }

            output.Slice(i, length - i).CopyTo(output.Slice(i + 1));
            output[i] = n;
            length++;
            i++;
        }

        return length;
    }

    /// <summary>
    /// Encodes code points as Punycode.
    /// </summary>
    /// <returns>The number of characters written, or -1 when the result does not fit or overflows.</returns>
    public static int Encode(ReadOnlySpan<int> input, Span<char> output)
    {
        int written = 0;
        foreach (int codePoint in input)
        {
            if (codePoint < 0x80)
            {
                if (written >= output.Length)
                {
                    return -1;
                }

                output[written++] = (char)codePoint;
            }
        }

        int basicCount = written;
        int handled = basicCount;
        if (basicCount > 0)
        {
            if (written >= output.Length)
            {
                return -1;
            }

            output[written++] = '-';
        }

        int n = initialN;
        int delta = 0;
        int bias = initialBias;
        while (handled < input.Length)
        {
            int m = int.MaxValue;
            foreach (int codePoint in input)
            {
                if (codePoint >= n && codePoint < m)
                {
                    m = codePoint;
                }
            }

            if (m - n > (int.MaxValue - delta) / (handled + 1))
            {
                return -1;
            }

            delta += (m - n) * (handled + 1);
            n = m;
            foreach (int codePoint in input)
            {
                if (codePoint < n && ++delta == int.MaxValue)
                {
                    return -1;
                }

                if (codePoint != n)
                {
                    continue;
                }

                int q = delta;
                for (int k = @base; ; k += @base)
                {
                    int t = Threshold(k, bias);
                    if (q < t)
                    {
                        break;
                    }

                    if (written >= output.Length)
                    {
                        return -1;
                    }

                    output[written++] = EncodeDigit(t + ((q - t) % (@base - t)));
                    q = (q - t) / (@base - t);
                }

                if (written >= output.Length)
                {
                    return -1;
                }

                output[written++] = EncodeDigit(q);
                bias = Adapt(delta, handled + 1, handled == basicCount);
                delta = 0;
                handled++;
            }

            delta++;
            n++;
        }

        return written;
    }

    private static int Threshold(int k, int bias) => k <= bias ? tmin : k >= bias + tmax ? tmax : k - bias;

    private static int Adapt(int delta, int pointCount, bool first)
    {
        delta = first ? delta / damp : delta / 2;
        delta += delta / pointCount;
        int k = 0;
        while (delta > ((@base - tmin) * tmax) / 2)
        {
            delta /= @base - tmin;
            k += @base;
        }

        return k + (((@base - tmin + 1) * delta) / (delta + skew));
    }

    private static int DecodeDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0' + 26,
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a',
        _ => @base,
    };

    private static char EncodeDigit(int digit) => (char)(digit < 26 ? 'a' + digit : '0' + digit - 26);
}
