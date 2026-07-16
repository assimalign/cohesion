using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// Transparent write-path wrapper over a response body stream: counts the bytes the
/// application writes and, when capture is armed, tees the first <c>limit</c> bytes into a
/// bounded buffer. Whether capture applies is decided lazily at the first write via the
/// supplied predicate, because the response <c>Content-Type</c> is unknown until the
/// application sets it. Writes pass straight through — the wrapper never buffers the full
/// body, so streaming responses and flow-control backpressure are unaffected.
/// </summary>
internal sealed class ResponseBodyCaptureStream : Stream
{
    private readonly Stream _inner;
    private readonly int _captureLimit;
    private Func<bool>? _capturePredicate;
    private bool _capture;
    private byte[]? _captured;
    private int _capturedCount;
    private long _bytesWritten;

    public ResponseBodyCaptureStream(Stream inner, int captureLimit, Func<bool>? capturePredicate)
    {
        _inner = inner;
        _captureLimit = captureLimit;
        _capturePredicate = capturePredicate;
    }

    /// <summary>Total bytes the application wrote to the body.</summary>
    public long BytesWritten => _bytesWritten;

    /// <summary>The captured body prefix; empty when capture never armed or nothing was written.</summary>
    public ReadOnlySpan<byte> Captured => _captured is null ? default : _captured.AsSpan(0, _capturedCount);

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanTimeout => _inner.CanTimeout;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }
    public override int ReadTimeout
    {
        get => _inner.ReadTimeout;
        set => _inner.ReadTimeout = value;
    }
    public override int WriteTimeout
    {
        get => _inner.WriteTimeout;
        set => _inner.WriteTimeout = value;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        Observe(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner.Write(buffer);
        Observe(buffer);
    }

    public override void WriteByte(byte value)
    {
        _inner.WriteByte(value);
        Observe(new ReadOnlySpan<byte>(in value));
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Observe(buffer.AsSpan(offset, count));
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span);
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private void Observe(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        _bytesWritten += data.Length;

        if (_capturePredicate is { } predicate)
        {
            // Evaluate once, at the first write - the application has set its response head
            // (including Content-Type) by the time it starts writing the body.
            _capture = _captureLimit > 0 && predicate();
            _capturePredicate = null;
        }

        if (!_capture || _capturedCount >= _captureLimit)
        {
            return;
        }

        _captured ??= new byte[_captureLimit];

        int take = Math.Min(data.Length, _captureLimit - _capturedCount);
        data[..take].CopyTo(_captured.AsSpan(_capturedCount));
        _capturedCount += take;
    }
}
