using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// Transparent read-path wrapper over a request body stream: counts the bytes the application
/// actually reads and, when capture is armed, tees the first <c>limit</c> bytes into a bounded
/// buffer. The body itself streams through untouched — no additional buffering, so transport
/// backpressure and request-size limits behave exactly as without the wrapper.
/// </summary>
internal sealed class RequestBodyCaptureStream : Stream
{
    private readonly Stream _inner;
    private readonly int _captureLimit;
    private byte[]? _captured;
    private int _capturedCount;
    private long _bytesRead;

    public RequestBodyCaptureStream(Stream inner, int captureLimit)
    {
        _inner = inner;
        _captureLimit = captureLimit;
    }

    /// <summary>Total bytes the application read from the body.</summary>
    public long BytesRead => _bytesRead;

    /// <summary>The captured body prefix; empty when capture was not armed or nothing was read.</summary>
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

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        Observe(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        int read = _inner.Read(buffer);
        Observe(buffer[..read]);
        return read;
    }

    public override int ReadByte()
    {
        int value = _inner.ReadByte();
        if (value >= 0)
        {
            byte b = (byte)value;
            Observe(new ReadOnlySpan<byte>(in b));
        }

        return value;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Observe(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Observe(buffer.Span[..read]);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
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

        _bytesRead += data.Length;

        if (_captureLimit <= 0 || _capturedCount >= _captureLimit)
        {
            return;
        }

        _captured ??= new byte[_captureLimit];

        int take = Math.Min(data.Length, _captureLimit - _capturedCount);
        data[..take].CopyTo(_captured.AsSpan(_capturedCount));
        _capturedCount += take;
    }
}
