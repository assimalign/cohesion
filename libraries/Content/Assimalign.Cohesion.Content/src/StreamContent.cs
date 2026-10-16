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
public sealed class StreamContent : IContent
{
    /// <summary>
    /// Creates read-only content over an existing stream.
    /// </summary>
    /// <param name="stream">The readable stream backing the content. Seekable streams produce reopenable content; non-seekable streams produce single-use content.</param>
    /// <param name="format">The format descriptor, or <see langword="null"/> for <see cref="ContentFormat.Unknown"/>.</param>
    /// <param name="name">The optional content name.</param>
    /// <param name="mediaType">The optional concrete media type of the instance.</param>
    /// <param name="leaveOpen">
    /// <see langword="false"/> (the default) to transfer ownership — disposing the content disposes the
    /// stream; <see langword="true"/> to borrow the stream and leave it open when the content is disposed.
    /// </param>
    /// <returns>Stream-backed content.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="stream"/> is not readable.</exception>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    public static StreamContent Create(Stream stream, ContentFormat? format = null, string? name = null, string? mediaType = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException(ErrorMessages.StreamNotReadable, nameof(stream));
        }

        return new StreamContent(stream, format ?? ContentFormat.Unknown, name, mediaType, leaveOpen);
    }


    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private bool _consumed;
    private bool _disposed;

    private StreamContent(Stream source, ContentFormat format, string? name, string? mediaType, bool leaveOpen)
    {
        _source = source;
        _leaveOpen = leaveOpen;
        Format = format;
        Name = name;
        MediaType = mediaType;
    }

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public ContentFormat Format { get; }

    /// <inheritdoc/>
    public string? MediaType { get; }

    /// <inheritdoc/>
    public long? Length => _source.CanSeek ? _source.Length : null;

    /// <inheritdoc/>
    public bool IsReadOnly => true;

    /// <inheritdoc/>
    public bool CanReopen => _source.CanSeek;

    /// <inheritdoc/>
    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_source.CanSeek)
        {
            _source.Seek(0, SeekOrigin.Begin);
        }
        else if (_consumed)
        {
            throw new ContentException(ErrorMessages.ContentAlreadyRead);
        }

        _consumed = true;
        return new NonDisposingReadView(_source);
    }

    /// <inheritdoc/>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(OpenRead());
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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
    private sealed class NonDisposingReadView : Stream
    {
        private bool _closed;
        private readonly Stream _inner;

        /// <summary>Initializes a new instance of the <see cref="NonDisposingReadView"/> class.</summary>
        /// <param name="inner">The content's backing stream the view reads through to.</param>
        public NonDisposingReadView(Stream inner)
        {
            _inner = inner;
        }

        /// <inheritdoc/>
        public override bool CanRead => !_closed && _inner.CanRead;

        /// <inheritdoc/>
        public override bool CanSeek => !_closed && _inner.CanSeek;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => _inner.Length;

        /// <inheritdoc/>
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        /// <inheritdoc/>
        public override void Flush()
        {
        }

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _inner.Read(buffer, offset, count);
        }

        /// <inheritdoc/>
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _inner.Read(buffer);
        }

        /// <inheritdoc/>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        /// <inheritdoc/>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _inner.Seek(offset, origin);
        }

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException(ErrorMessages.ContentReadOnly);

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(ErrorMessages.ContentReadOnly);

        protected override void Dispose(bool disposing)
        {
            _closed = true;
            base.Dispose(disposing);
        }
    }
}
