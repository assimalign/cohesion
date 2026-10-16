using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Content.Pdf.Tests;

internal sealed class PdfFailingWriteStream : Stream
{
    private readonly CancellationTokenSource? _cancellation;
    internal long Written { get; private set; }
    internal PdfFailingWriteStream(CancellationTokenSource? cancellation = null) => _cancellation = cancellation;
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_cancellation is null) { throw new IOException("Injected output failure."); }
        Written += buffer.Length; _cancellation.Cancel();
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override bool CanRead => false;
    public override long Position { get => Written; set => throw new NotSupportedException(); }
    public override long Length => Written;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
