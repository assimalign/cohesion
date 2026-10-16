using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Mkv.Tests;

internal sealed class ForwardWriteStream : Stream
{
    private readonly Stream _output;
    internal ForwardWriteStream(Stream output) { _output = output; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Flush() => _output.Flush();
    public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _output.Write(buffer);
}
