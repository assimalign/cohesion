using System;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Ebml.Internal;

// A counted, bounded view lets Content.Binary own exact-read loops while EBML supplies
// absolute positions even when the original source cannot seek.
internal sealed class EbmlInput : Stream
{
    private readonly Stream _source;
    private long _position;

    internal EbmlInput(Stream source)
    {
        _source = source;
        _position = source is ContentSliceStream slice ? slice.SourceOffset + slice.Position
            : source.CanSeek || source is EbmlInput ? source.Position : 0;
    }

    internal long? End { get; set; }
    internal void Advance(long count) => _position += count;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override int Read(Span<byte> buffer)
    {
        if (End is long end)
        {
            buffer = buffer[..(int)Math.Min(buffer.Length, Math.Max(0, end - _position))];
        }

        var read = _source.Read(buffer);
        _position += read;
        return read;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
