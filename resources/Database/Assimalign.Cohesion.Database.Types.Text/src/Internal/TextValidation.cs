using System;
using System.Buffers;
using System.Text;

namespace Assimalign.Cohesion.Database.Types.Text.Internal;

internal static class TextValidation
{
    internal static int Utf8Length(string value)
    {
        ReadOnlySpan<char> remaining = value;
        int length = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed) != OperationStatus.Done)
            {
                throw new TextException(TextErrorCode.InvalidUnicode, ErrorMessages.InvalidUnicode);
            }

            // Callers need an exact count only for admissible lexemes. Saturate
            // oversized input while still validating every scalar, avoiding overflow.
            length = Math.Min(TextVector.MaxLexemeBytes + 1, length + rune.Utf8SequenceLength);
            remaining = remaining[consumed..];
        }

        return length;
    }

    // UTF-8 order is scalar order; UTF-16 ordinal order disagrees above U+FFFF.
    internal static int Compare(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            int a = char.ConvertToUtf32(left, i), b = char.ConvertToUtf32(right, j);
            if (a != b)
            {
                return a.CompareTo(b);
            }

            i += a > 0xffff ? 2 : 1;
            j += b > 0xffff ? 2 : 1;
        }

        return (left.Length - i).CompareTo(right.Length - j);
    }
}
