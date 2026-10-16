using System;
using System.IO;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

internal sealed class PdfTestStream : Stream
{
    private readonly Stream _source;
    private readonly bool _seekable;
    internal PdfTestStream(Stream source, bool seekable) { _source = source; _seekable = seekable; }
    public override bool CanRead => true;
    public override bool CanSeek => _seekable;
    public override bool CanWrite => false;
    public override long Length => _seekable ? _source.Length : throw new NotSupportedException();
    public override long Position { get => _seekable ? _source.Position : throw new NotSupportedException(); set { if (!_seekable) { throw new NotSupportedException(); } _source.Position = value; } }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => _source.Read(buffer[..Math.Min(buffer.Length, 1)]);
    public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { _source.Dispose(); } base.Dispose(disposing); }
}
