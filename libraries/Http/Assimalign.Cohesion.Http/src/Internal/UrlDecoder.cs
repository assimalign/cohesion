using System;
using System.Text;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Http.Internal;

internal sealed class UrlDecoder
{
    private static ReadOnlySpan<sbyte> CharToHexLookup => new sbyte[256]
    {
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, 0, 1,
        2, 3, 4, 5, 6, 7, 8, 9, -1, -1,
        -1, -1, -1, -1, -1, 10, 11, 12, 13, 14,
        15, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, 10, 11, 12,
        13, 14, 15, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
        -1, -1, -1, -1, -1, -1
    };

    public static int DecodeRequestLine(ReadOnlySpan<byte> source, Span<byte> destination, bool isFormEncoding)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException(ErrorMessages.DestinationByteSpanTooShort, "destination");
        }
        source.CopyTo(destination);
        return DecodeInPlace(destination.Slice(0, source.Length), isFormEncoding);
    }

    public static int DecodeInPlace(Span<byte> buffer, bool isFormEncoding)
    {
        int num = 0;
        int destinationIndex = 0;
        while (num != buffer.Length)
        {
            if (buffer[num] == 43 && isFormEncoding)
            {
                buffer[num] = 32;
            }
            else if (buffer[num] == 37)
            {
                int sourceIndex = num;
                if (!DecodeCore(ref sourceIndex, ref destinationIndex, buffer, isFormEncoding))
                {
                    Copy(num, sourceIndex, ref destinationIndex, buffer);
                }
                num = sourceIndex;
            }
            else
            {
                buffer[destinationIndex++] = buffer[num++];
            }
        }
        return destinationIndex;
    }

    private static bool DecodeCore(ref int sourceIndex, ref int destinationIndex, Span<byte> buffer, bool isFormEncoding)
    {
        int num = UnescapePercentEncoding(ref sourceIndex, buffer, isFormEncoding);
        if (num == -1)
        {
            return false;
        }
        if (num == 0)
        {
            throw new InvalidOperationException(ErrorMessages.PathNullCharacter);
        }
        if (num <= 127)
        {
            buffer[destinationIndex++] = (byte)num;
            return true;
        }
        int num2 = 0;
        int num3 = 0;
        int num4 = 0;
        int num5;
        int num6;
        int num7;
        if ((num & 0xE0) == 192)
        {
            num5 = num & 0x1F;
            num6 = 2;
            num7 = 128;
        }
        else if ((num & 0xF0) == 224)
        {
            num5 = num & 0xF;
            num6 = 3;
            num7 = 2048;
        }
        else
        {
            if ((num & 0xF8) != 240)
            {
                return false;
            }
            num5 = num & 7;
            num6 = 4;
            num7 = 65536;
        }
        int num8 = num6 - 1;
        while (num8 > 0)
        {
            if (sourceIndex == buffer.Length)
            {
                return false;
            }
            int scan = sourceIndex;
            int num9 = UnescapePercentEncoding(ref scan, buffer, isFormEncoding);
            if (num9 == -1)
            {
                return false;
            }
            if ((num9 & 0xC0) != 128)
            {
                return false;
            }
            num5 = (num5 << 6) | (num9 & 0x3F);
            num8--;
            if (num8 == 1 && num5 >= 864 && num5 <= 895)
            {
                return false;
            }
            if (num8 == 2 && num5 >= 272)
            {
                return false;
            }
            sourceIndex = scan;
            if (num6 - num8 == 2)
            {
                num2 = num9;
            }
            else if (num6 - num8 == 3)
            {
                num3 = num9;
            }
            else if (num6 - num8 == 4)
            {
                num4 = num9;
            }
        }
        if (num5 < num7)
        {
            return false;
        }
        if (num6 > 0)
        {
            buffer[destinationIndex++] = (byte)num;
        }
        if (num6 > 1)
        {
            buffer[destinationIndex++] = (byte)num2;
        }
        if (num6 > 2)
        {
            buffer[destinationIndex++] = (byte)num3;
        }
        if (num6 > 3)
        {
            buffer[destinationIndex++] = (byte)num4;
        }
        return true;
    }

    private static void Copy<T>(int begin, int end, ref int writer, Span<T> buffer)
    {
        while (begin != end)
        {
            buffer[writer++] = buffer[begin++];
        }
    }

    private static int UnescapePercentEncoding(ref int scan, Span<byte> buffer, bool isFormEncoding)
    {
        if (buffer[scan++] != 37)
        {
            return -1;
        }
        int scan2 = scan;
        int num = ReadHex(ref scan2, buffer);
        if (num == -1)
        {
            return -1;
        }
        int num2 = ReadHex(ref scan2, buffer);
        if (num2 == -1)
        {
            return -1;
        }
        if (SkipUnescape(num, num2, isFormEncoding))
        {
            return -1;
        }
        scan = scan2;
        return (num << 4) + num2;
    }

    private static int ReadHex(ref int scan, Span<byte> buffer)
    {
        if (scan == buffer.Length)
        {
            return -1;
        }
        byte b = buffer[scan++];
        if ((b < 48 || b > 57) && (b < 65 || b > 70) && (b < 97 || b > 102))
        {
            return -1;
        }
        if (b <= 57)
        {
            return b - 48;
        }
        if (b <= 70)
        {
            return b - 65 + 10;
        }
        return b - 97 + 10;
    }

    private static bool SkipUnescape(int value1, int value2, bool isFormEncoding)
    {
        if (isFormEncoding)
        {
            return false;
        }
        if (value1 == 2 && value2 == 15)
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Determines whether <see cref="DecodeInPlace(Span{char})"/>, reaching the <c>%</c> that starts
    /// <paramref name="text"/>, decodes the escape there instead of leaving it as written. It leaves an
    /// encoded slash, an escape without two hex digits, and a sequence that is not valid UTF-8 (invalid,
    /// overlong, truncated, or a surrogate). It never leaves <c>%00</c>: it refuses it.
    /// </summary>
    /// <param name="text">The text from the <c>%</c> on.</param>
    /// <returns><see langword="true"/> when the decoder does not leave the escape as written.</returns>
    public static bool DecodesEscapeAt(ReadOnlySpan<char> text)
    {
        // The decoder throws for a decoded NUL rather than leaving it, so the probe answers for it.
        if (text.Length >= 3 && text[1] == '0' && text[2] == '0')
        {
            return true;
        }

        // One scalar value is at most four escapes. DecodeCore writes what it decodes over the text it has
        // read, so it runs on a copy.
        Span<char> window = stackalloc char[12];
        int length = Math.Min(text.Length, window.Length);
        text[..length].CopyTo(window);

        int sourceIndex = 0;
        int destinationIndex = 0;
        return DecodeCore(ref sourceIndex, ref destinationIndex, window[..length]);
    }

    public static int DecodeRequestLine(ReadOnlySpan<char> source, Span<char> destination)
    {
        source.CopyTo(destination);
        return DecodeInPlace(destination.Slice(0, source.Length));
    }

    public static int DecodeInPlace(Span<char> buffer)
    {
        int num = buffer.IndexOf('%');
        if (num == -1)
        {
            return buffer.Length;
        }
        int num2 = num;
        int destinationIndex = num;
        while (num2 != buffer.Length)
        {
            if (buffer[num2] == '%')
            {
                int sourceIndex = num2;
                if (!DecodeCore(ref sourceIndex, ref destinationIndex, buffer))
                {
                    Copy(num2, sourceIndex, ref destinationIndex, buffer);
                }
                num2 = sourceIndex;
            }
            else
            {
                buffer[destinationIndex++] = buffer[num2++];
            }
        }
        return destinationIndex;
    }

    private static bool DecodeCore(ref int sourceIndex, ref int destinationIndex, Span<char> buffer)
    {
        int num = UnescapePercentEncoding(ref sourceIndex, buffer);
        if (num == -1)
        {
            return false;
        }
        if (num == 0)
        {
            throw new InvalidOperationException(ErrorMessages.PathNullCharacter);
        }
        if (num <= 127)
        {
            buffer[destinationIndex++] = (char)num;
            return true;
        }
        int num2;
        int num3;
        int num4;
        if ((num & 0xE0) == 192)
        {
            num2 = num & 0x1F;
            num3 = 2;
            num4 = 128;
        }
        else if ((num & 0xF0) == 224)
        {
            num2 = num & 0xF;
            num3 = 3;
            num4 = 2048;
        }
        else
        {
            if ((num & 0xF8) != 240)
            {
                return false;
            }
            num2 = num & 7;
            num3 = 4;
            num4 = 65536;
        }
        int num5 = num3 - 1;
        while (num5 > 0)
        {
            if (sourceIndex == buffer.Length)
            {
                return false;
            }
            int scan = sourceIndex;
            int num6 = UnescapePercentEncoding(ref scan, buffer);
            if (num6 == -1)
            {
                return false;
            }
            if ((num6 & 0xC0) != 128)
            {
                return false;
            }
            num2 = (num2 << 6) | (num6 & 0x3F);
            num5--;
            sourceIndex = scan;
        }
        if (num2 < num4)
        {
            return false;
        }
        if (!Rune.TryCreate(num2, out var result) || !result.TryEncodeToUtf16(buffer.Slice(destinationIndex), out var charsWritten))
        {
            return false;
        }
        destinationIndex += charsWritten;
        return true;
    }

    private static int UnescapePercentEncoding(ref int scan, ReadOnlySpan<char> buffer)
    {
        if (buffer[scan++] != '%')
        {
            return -1;
        }
        int scan2 = scan;
        int num = ReadHex(ref scan2, buffer);
        int num2 = ReadHex(ref scan2, buffer);
        int num3 = (num << 4) | num2;
        if (num3 < 0 || num3 == 47)
        {
            return -1;
        }
        scan = scan2;
        return num3;
    }

    private static int ReadHex(ref int scan, ReadOnlySpan<char> buffer)
    {
        int num = scan++;
        if ((uint)num >= (uint)buffer.Length)
        {
            return -1;
        }
        return FromChar(buffer[num]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FromChar(int c)
    {
        if ((uint)c < (uint)CharToHexLookup.Length)
        {
            return CharToHexLookup[c];
        }
        return -1;
    }
}
