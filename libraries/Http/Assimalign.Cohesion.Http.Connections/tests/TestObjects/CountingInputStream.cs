using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A read-only peer stream that serves a prefix, then a run of one filler octet, then an optional
/// suffix, then ends, and counts every octet a reader takes from it. It stands in for a peer that
/// keeps sending: <see cref="Consumed"/> shows how far a reader got before it stopped. Reads complete
/// synchronously, so a reader that awaits only this stream runs on the calling thread, where
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> sees everything it allocates.
/// </summary>
internal sealed class CountingInputStream : Stream
{
    private readonly byte[] _prefix;
    private readonly byte _filler;
    private readonly long _fillerLength;
    private readonly byte[] _suffix;

    public CountingInputStream(byte[] prefix, byte filler, long fillerLength, byte[]? suffix = null)
    {
        _prefix = prefix;
        _filler = filler;
        _fillerLength = fillerLength;
        _suffix = suffix ?? [];
    }

    /// <summary>Gets the number of octets the reader has taken.</summary>
    public long Consumed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int written = 0;

        while (written < buffer.Length)
        {
            long position = Consumed;

            if (position < _prefix.Length)
            {
                buffer[written] = _prefix[position];
            }
            else if (position < _prefix.Length + _fillerLength)
            {
                buffer[written] = _filler;
            }
            else if (position < _prefix.Length + _fillerLength + _suffix.Length)
            {
                buffer[written] = _suffix[position - _prefix.Length - _fillerLength];
            }
            else
            {
                break;
            }

            written++;
            Consumed++;
        }

        return written;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
