using System;
using System.Collections.Generic;

namespace System.Linq;

public static class LinqExtensions
{
    public static bool ContainsAny<T>(this IEnumerable<T> enumerable, IEnumerable<T> values)
    {
        foreach (var value in values)
        {
            if (enumerable.Contains(value))
            {
                return true;
            }
        }
        return false;
    }

    public static bool ContainsAny<T>(this T[] array, T[] values)
    {
        if (array is null)
        {
            return false;
        }
        for (int i = 0; i < array.Length; i++)
        {
            var item = array[i]!;

            for (int a = 0; a < values.Length; a++)
            {
                if (item.Equals(values[a]))
                {
                    return true;
                }
            }
        }

        return false;
    }


    public static int SplitAny(this ReadOnlySpan<char> source, Span<Range> destination, ReadOnlySpan<char> separators)
    {
#if NET7_0_OR_GREATER
        return MemoryExtensions.SplitAny(source, destination, separators);
#else
        var count = 0;
        var start = 0;

        for (int i = 0; i < source.Length; i++)
        {
            if ((i + 1) == source.Length)
            {
                destination[count] = new Range(start, i);
                count++;
                break;
            }
            for (int a = 0; a < separators.Length; a++)
            {
                if (source[i] == separators[a])
                {
                    destination[count] = new Range(start, i);
                    start = (i + 1);
                    count++;
                }
            }
        }

        return count;
#endif
    }
}
