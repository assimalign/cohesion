using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Mpeg.Tests;

// A zero-filled, seekable virtual source with two authored metadata regions, never a large array.
internal sealed class Mp4SparseStream : Stream
{
    private readonly byte[] _prefix;
    private readonly byte[] _suffix;
    private readonly long _suffixOffset;
    internal Mp4SparseStream(byte[] prefix, byte[] suffix, long suffixOffset)
    {
        _prefix = prefix;
        _suffix = suffix;
        _suffixOffset = suffixOffset;
        Length = suffixOffset + suffix.Length;
    }
    internal long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }
    public override long Position { get; set; }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, Length - Position);
        var destination = buffer[..count];
        destination.Clear();
        CopyRegion(_prefix, 0, destination);
        CopyRegion(_suffix, _suffixOffset, destination);
        Position += count;
        BytesRead += count;
        return count;
    }
    private void CopyRegion(byte[] bytes, long start, Span<byte> destination)
    {
        var from = Math.Max(start, Position);
        var to = Math.Min(start + bytes.Length, Position + destination.Length);
        if (to > from) { bytes.AsSpan((int)(from - start), (int)(to - from)).CopyTo(destination[(int)(from - Position)..]); }
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => Length + offset
    };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
