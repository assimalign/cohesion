using System;
using System.Buffers;

namespace Assimalign.Cohesion.Content.Text.Tests;

internal static class TestSequenceFactory
{
    public static ReadOnlySequence<char> Create(params string[] segments)
    {
        if (segments.Length == 1)
        {
            return new ReadOnlySequence<char>(segments[0].AsMemory());
        }

        var first = new Segment(segments[0].AsMemory(), 0);
        var last = first;
        for (var i = 1; i < segments.Length; i++)
        {
            last = last.Append(segments[i].AsMemory());
        }

        return new ReadOnlySequence<char>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<char>
    {
        public Segment(ReadOnlyMemory<char> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<char> memory)
        {
            var segment = new Segment(memory, RunningIndex + Memory.Length);
            Next = segment;
            return segment;
        }
    }
}
