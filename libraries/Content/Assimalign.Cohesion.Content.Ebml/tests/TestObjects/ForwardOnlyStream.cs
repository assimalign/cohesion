using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Ebml.Tests.TestObjects;

internal sealed class ForwardOnlyStream : Stream
{
    private readonly Stream _source;
    internal ForwardOnlyStream(Stream source) { _source = source; }
    public override bool CanRead => _source.CanRead;
    public override bool CanWrite => _source.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, Math.Min(1, count));
    public override int Read(Span<byte> buffer) => _source.Read(buffer[..Math.Min(1, buffer.Length)]);
    public override void Write(byte[] buffer, int offset, int count) => _source.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _source.Write(buffer);
    public override void Flush() => _source.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
