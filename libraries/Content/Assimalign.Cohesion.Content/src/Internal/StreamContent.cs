using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// Stream-backed, read-only content. Seekable sources are reopenable (each read rewinds to the start);
/// non-seekable sources are single-use. The source is owned or borrowed per the creation-time
/// <c>leaveOpen</c> flag.
/// </summary>
/// <remarks>
/// Reads returned by <see cref="OpenRead"/> are views over the single underlying stream, so concurrent
/// readers are not supported for stream-backed content; use in-memory content when concurrency is needed.
/// </remarks>
internal sealed class StreamContent : IContent
{
    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private bool _consumed;
    private bool _disposed;

    internal StreamContent(Stream source, ContentFormat format, string? name, string? mediaType, bool leaveOpen)
    {
        _source = source;
        _leaveOpen = leaveOpen;
        Format = format;
        Name = name;
        MediaType = mediaType;
    }

    public string? Name { get; }

    public ContentFormat Format { get; }

    public string? MediaType { get; }

    public long? Length => _source.CanSeek ? _source.Length : null;

    public bool IsReadOnly => true;

    public bool CanReopen => _source.CanSeek;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_source.CanSeek)
        {
            _source.Seek(0, SeekOrigin.Begin);
        }
        else if (_consumed)
        {
            throw new ContentException("The content is single-use because its backing stream is not seekable, and it has already been read.");
        }

        _consumed = true;
        return new NonDisposingReadView(_source);
    }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(OpenRead());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _source.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A caller-owned, read-only pass-through view over the content's backing stream. Disposing the view
    /// does not dispose the backing stream — the content instance controls the source's lifetime.
    /// </summary>
    private sealed class NonDisposingReadView(Stream inner) : Stream
    {
        private bool _closed;

        public override bool CanRead => !_closed && inner.CanRead;

        public override bool CanSeek => !_closed && inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return inner.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed = true;
            base.Dispose(disposing);
        }
    }
}
