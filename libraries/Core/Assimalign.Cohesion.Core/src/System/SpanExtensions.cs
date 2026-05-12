namespace System;

public static class SpanExtensions
{
    extension(ReadOnlySpan<char> source)
    {
        public int SplitAny(Span<Range> destination, ReadOnlySpan<char> separators)
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
}
