using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

internal sealed class ProbeStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _seekable;
    internal ProbeStream(Stream inner, bool seekable) { _inner = inner; _seekable = seekable; }
    internal long BytesRead { get; private set; }
    internal int MaxReadRequest { get; private set; }
    internal bool Disposed { get; private set; }
    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => _seekable;
    public override long Length => _seekable ? _inner.Length : throw new NotSupportedException();
    public override long Position { get => _seekable ? _inner.Position : throw new NotSupportedException(); set { if (!_seekable) { throw new NotSupportedException(); } _inner.Position = value; } }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        MaxReadRequest = Math.Max(MaxReadRequest, buffer.Length);
        var count = _inner.Read(buffer);
        BytesRead += count;
        return count;
    }
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _seekable ? _inner.Seek(offset, origin) : throw new NotSupportedException();
    public override void Flush() => _inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
