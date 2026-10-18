using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>A synchronous peer whose consumed count excludes buffered read-ahead.</summary>
internal sealed class CountingInputReader : PipeReader
{
    private readonly ReadOnlySequence<byte> _bytes;
    private ReadOnlySequence<byte> _pending;

    public CountingInputReader(byte[] prefix, byte filler, long fillerLength, byte[]? suffix = null, int segmentSize = int.MaxValue)
    {
        byte[] bytes = new byte[checked(prefix.Length + (int)fillerLength + (suffix?.Length ?? 0))];
        prefix.CopyTo(bytes, 0);
        bytes.AsSpan(prefix.Length, (int)fillerLength).Fill(filler);
        suffix?.CopyTo(bytes, prefix.Length + (int)fillerLength);
        if (bytes.Length <= segmentSize)
        {
            _bytes = new ReadOnlySequence<byte>(bytes);
        }
        else
        {
            Segment first = new(bytes.AsMemory(0, segmentSize));
            Segment last = first;
            for (int offset = segmentSize; offset < bytes.Length; offset += segmentSize)
            {
                last = last.Append(bytes.AsMemory(offset, Math.Min(segmentSize, bytes.Length - offset)));
            }
            _bytes = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        }
    }

    public long Consumed { get; private set; }
    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        => Consumed += _pending.Slice(0, consumed).Length;
    public override void CancelPendingRead() { }
    public override void Complete(Exception? exception = null) { }
    public override bool TryRead(out ReadResult result)
    {
        _pending = _bytes.Slice(Consumed);
        result = new ReadResult(_pending, isCanceled: false, isCompleted: true);
        return true;
    }
    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TryRead(out ReadResult result);
        return new ValueTask<ReadResult>(result);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
