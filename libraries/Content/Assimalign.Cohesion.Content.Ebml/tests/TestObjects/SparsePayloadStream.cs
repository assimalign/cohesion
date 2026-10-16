using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

// A virtual file with an in-memory EBML prefix and a 40 GiB zero payload; never allocates that payload.
internal sealed class SparsePayloadStream : Stream
{
    private readonly byte[] _prefix;
    private readonly long _length;
    private long _position;
    internal SparsePayloadStream(byte[] prefix, long payloadLength) { _prefix = prefix; _length = prefix.Length + payloadLength; }
    internal long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => true;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value; }
    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, Math.Max(0, _length - _position));
        buffer[..count].Clear();
        if (_position < _prefix.Length)
        {
            var prefixCount = (int)Math.Min(count, _prefix.Length - _position);
            _prefix.AsSpan((int)_position, prefixCount).CopyTo(buffer);
        }
        _position += count;
        BytesRead += count;
        return count;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? _position : _length) + offset;
        return _position;
    }
    public override void Flush() { }
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
