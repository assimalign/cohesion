using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

/// <summary>
/// A read-only stream that returns its data and then fails the next read with an
/// <see cref="IOException"/>: a client whose upload breaks off, or a destination whose body does.
/// </summary>
internal sealed class FailingReadStream : Stream
{
    private readonly byte[] _data;
    private int _position;

    public FailingReadStream(byte[] data)
    {
        _data = data;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _data.Length)
        {
            throw new IOException("The test stream broke off.");
        }

        int count = Math.Min(buffer.Length, _data.Length - _position);
        _data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }
        catch (IOException exception)
        {
            return ValueTask.FromException<int>(exception);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
