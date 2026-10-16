using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

// A synthetic 40 GiB mdat followed by free. Any attempt to read media bytes fails immediately.
internal sealed class SparseMediaStream : Stream
{
    internal const long MediaSize = 40L * 1024 * 1024 * 1024;
    private readonly byte[] _header = new byte[16];
    private readonly byte[] _tail;
    internal SparseMediaStream(byte[]? tail = null)
    {
        _tail = tail ?? BmffFixtures.Box("free");
        BinaryPrimitives.WriteUInt32BigEndian(_header, 1);
        FourCC.Parse("mdat").WriteTo(_header.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(_header.AsSpan(8), (ulong)(MediaSize + 16));
    }
    internal int BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => true;
    public override long Length => MediaSize + 16 + _tail.Length;
    public override long Position { get; set; }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (Position >= Length) { return 0; }
        if (Position >= 16 && Position < MediaSize + 16) { throw new InvalidOperationException("Media payload was read."); }
        var bytes = Position < 16 ? _header.AsSpan((int)Position) : _tail.AsSpan((int)(Position - MediaSize - 16));
        var count = Math.Min(bytes.Length, buffer.Length);
        bytes[..count].CopyTo(buffer);
        Position += count;
        BytesRead += count;
        return count;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset;
        return Position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
