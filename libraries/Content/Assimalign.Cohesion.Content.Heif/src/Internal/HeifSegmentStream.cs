using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Heif.Internal;

// A seekable concatenation of borrowed segments. Used for idat appends and fragmented descriptors.
// Only a current view is opened, never a payload array; disposal leaves backing sources open.
internal sealed class HeifSegmentStream : Stream
{
    private readonly BinarySegment[] _segments;
    private readonly long[] _starts;
    private readonly long _length;
    private long _position;
    private bool _disposed;
    internal HeifSegmentStream(IReadOnlyList<BinarySegment> segments)
    {
        _segments = new BinarySegment[segments.Count];
        _starts = new long[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            _starts[i] = _length;
            _segments[i] = segments[i];
            if (segments[i].Length > long.MaxValue - _length)
            {
                throw new ContentFormatException(ErrorMessages.LimitExceeded, ContentFormatErrorCode.LimitExceeded);
            }

            _length += segments[i].Length;
        }
    }
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_position >= _length || buffer.IsEmpty)
        {
            return 0;
        }

        var low = 0;
        var high = _starts.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_starts[middle] <= _position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        var index = low - 1;
        var offset = _position - _starts[index];
        var length = (int)Math.Min(buffer.Length, _segments[index].Length - offset);
        using var stream = _segments[index].Slice(offset, length).OpenRead();
        stream.ReadContentExactly(buffer[..length]);
        _position += length;
        return length;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var start = origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? _position : _length;
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, -start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, long.MaxValue - start);
        return _position = start + offset;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException(ErrorMessages.InvalidEdit);
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(ErrorMessages.InvalidEdit);
    protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
}
