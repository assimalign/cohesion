using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

internal sealed class MkvCountingStream : Stream
{
    private long _length;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(ErrorMessages.StreamOperationUnsupported); }
    public override void Write(byte[] buffer, int offset, int count) => _length += count;
    public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    public override void WriteByte(byte value) => _length++;
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(ErrorMessages.StreamOperationUnsupported);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(ErrorMessages.StreamOperationUnsupported);
    public override void SetLength(long value) => throw new NotSupportedException(ErrorMessages.StreamOperationUnsupported);
}
