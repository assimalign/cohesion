using System;
using System.IO;

namespace Assimalign.IO.Ebml.Tests.TestObjects;

/// <summary>
/// A read-only stream that yields at most a fixed number of bytes per <see cref="Read(byte[], int, int)"/>
/// call, modelling the partial reads a network or pipe-backed stream is free to return. Any reader that
/// assumes a single <c>Read</c> satisfies the full request will observe truncated data against this stream.
/// </summary>
internal sealed class ChunkedReadStream : Stream
{
    private readonly byte[] _data;
    private readonly int _chunkSize;
    private int _position;

    public ChunkedReadStream(byte[] data, int chunkSize)
    {
        _data = data;
        _chunkSize = chunkSize;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _data.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _data.Length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var toCopy = Math.Min(Math.Min(count, _chunkSize), remaining);
        Array.Copy(_data, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
