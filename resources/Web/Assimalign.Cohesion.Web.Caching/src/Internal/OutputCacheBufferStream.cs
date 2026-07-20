using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Caching.Internal;

/// <summary>
/// The response-body wrapper the middleware installs on a cache miss: a <em>tee</em> that writes every
/// byte straight through to the transport's real response body (so the client is always served) while
/// also capturing a copy for storage, up to a per-entry size cap. Crossing the cap abandons the capture
/// — the buffer is released and writes continue to flow through untouched — so an over-large response is
/// streamed normally and simply not cached.
/// </summary>
/// <remarks>
/// The stream never holds a second full copy beyond the cap: once <see cref="IsCapExceeded"/> trips it
/// keeps only the pass-through path. It mirrors the no-clobber discipline of the compression body wrapper
/// — the transport's own body is written first, so caching is a strictly additive side effect that can
/// never corrupt or withhold the response.
/// </remarks>
internal sealed class OutputCacheBufferStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private MemoryStream? _capture;
    private bool _capExceeded;

    public OutputCacheBufferStream(Stream inner, long maxBytes)
    {
        _inner = inner;
        _maxBytes = maxBytes;
        _capture = new MemoryStream();
    }

    /// <summary>Gets a value indicating whether the response body outgrew the per-entry cap (caching abandoned).</summary>
    public bool IsCapExceeded => _capExceeded;

    /// <summary>
    /// Gets the captured body bytes, or <see langword="null"/> when the cap was exceeded and caching was
    /// abandoned.
    /// </summary>
    public byte[]? CapturedBytes => _capExceeded ? null : _capture!.ToArray();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // The client is served first and unconditionally: caching is an additive side effect.
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Capture(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        // The wrapped body is the transport's in-memory response buffer, so this completes synchronously
        // without blocking on real I/O; handlers overwhelmingly use the async path.
        _inner.Write(buffer, offset, count);
        Capture(new ReadOnlySpan<byte>(buffer, offset, count));
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    private void Capture(ReadOnlySpan<byte> bytes)
    {
        if (_capExceeded)
        {
            return;
        }

        if (_capture!.Length + bytes.Length > _maxBytes)
        {
            // Over the per-entry cap: drop the partial capture and stop accumulating. The write-through
            // above already delivered these bytes, so the response is unaffected.
            _capExceeded = true;
            _capture.Dispose();
            _capture = null;
            return;
        }

        _capture.Write(bytes);
    }
}
