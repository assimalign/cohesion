using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

internal sealed class SparseReadStream : Stream
{
    private readonly byte[] _prefix;
    private readonly long _length;
    private long _position;
    internal SparseReadStream(byte[] prefix, long length) { _prefix = prefix; _length = length; }
    internal long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = value; }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _length - _position);
        buffer[..count].Clear();
        if (_position < _prefix.Length)
        {
            var available = (int)Math.Min(count, _prefix.Length - _position);
            _prefix.AsSpan((int)_position, available).CopyTo(buffer);
        }
        _position += count; BytesRead += count; return count;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => Length + offset };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
