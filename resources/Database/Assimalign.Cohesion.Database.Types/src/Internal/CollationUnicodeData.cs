using System;
using System.Text;

namespace Assimalign.Cohesion.Database.Types;

/// <summary>Applies the pinned Unicode mappings without runtime globalization services.</summary>
internal static partial class CollationUnicodeData
{
    internal static int FoldCase(int scalar)
    {
        int index = Find(scalar, CaseMappings, 2);
        return index < 0 ? scalar : CaseMappings[index + 1];
    }

    internal static void AppendWithoutAccents(StringBuilder result, int scalar)
    {
        // UAX #15 Hangul decomposition is algorithmic and absent from UnicodeData.txt.
        const int hangulBase = 0xAC00;
        const int hangulCount = 11172;
        int syllable = scalar - hangulBase;
        if ((uint)syllable < hangulCount)
        {
            result.Append((char)(0x1100 + syllable / 588));
            result.Append((char)(0x1161 + syllable % 588 / 28));
            if (syllable % 28 != 0)
            {
                result.Append((char)(0x11A7 + syllable % 28));
            }

            return;
        }

        int index = Find(scalar, CanonicalDecompositions, 3);
        if (index >= 0)
        {
            AppendWithoutAccents(result, CanonicalDecompositions[index + 1]);
            if (CanonicalDecompositions[index + 2] != 0)
            {
                AppendWithoutAccents(result, CanonicalDecompositions[index + 2]);
            }

            return;
        }

        // Canonical ordering only reorders combining marks. All Unicode 17.0
        // characters with a nonzero combining class are marks removed here.
        if (!IsMark(scalar))
        {
            result.Append(new Rune(FoldCase(scalar)).ToString());
        }
    }

    private static bool IsMark(int scalar)
    {
        ReadOnlySpan<int> ranges = MarkRanges;
        int low = 0, high = ranges.Length / 2 - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (scalar < ranges[middle * 2])
            {
                high = middle - 1;
            }
            else if (scalar > ranges[middle * 2 + 1])
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static int Find(int scalar, ReadOnlySpan<int> table, int width)
    {
        int low = 0, high = table.Length / width - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            int key = table[middle * width];
            if (scalar < key)
            {
                high = middle - 1;
            }
            else if (scalar > key)
            {
                low = middle + 1;
            }
            else
            {
                return middle * width;
            }
        }

        return -1;
    }
}
