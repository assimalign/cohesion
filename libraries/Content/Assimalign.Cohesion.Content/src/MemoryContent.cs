using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// In-memory content. Always reopenable; writable unless created read-only. Writes are committed when
/// the write stream is disposed, atomically replacing the visible bytes.
/// </summary>
public sealed class MemoryContent : IWritableContent
{
    /// <summary>
    /// Creates read-only, reopenable content over a block of bytes.
    /// </summary>
    /// <param name="data">The bytes backing the content. The content keeps a reference; the caller must not mutate the memory afterwards.</param>
    /// <param name="format">The format descriptor, or <see langword="null"/> for <see cref="ContentFormat.Unknown"/>.</param>
    /// <param name="name">The optional content name.</param>
    /// <param name="mediaType">The optional concrete media type of the instance.</param>
    /// <returns>Read-only in-memory content.</returns>
    public static MemoryContent Create(ReadOnlyMemory<byte> data, ContentFormat? format = null, string? name = null, string? mediaType = null) =>
        new MemoryContent(data, format ?? ContentFormat.Unknown, name, mediaType, isReadOnly: true);

    /// <summary>
    /// Creates an empty, writable in-memory buffer that format writers can emit into.
    /// </summary>
    /// <param name="format">The format descriptor, or <see langword="null"/> for <see cref="ContentFormat.Unknown"/>.</param>
    /// <param name="name">The optional content name.</param>
    /// <param name="mediaType">The optional concrete media type of the instance.</param>
    /// <returns>Writable in-memory content whose bytes are replaced on each committed write.</returns>
    public static MemoryContent CreateWritable(ContentFormat? format = null, string? name = null, string? mediaType = null) =>
        new MemoryContent(ReadOnlyMemory<byte>.Empty, format ?? ContentFormat.Unknown, name, mediaType, isReadOnly: false);


    private ReadOnlyMemory<byte> _data;
    private bool _disposed;

    private MemoryContent(ReadOnlyMemory<byte> data, ContentFormat format, string? name, string? mediaType, bool isReadOnly)
    {
        _data = data;
        Format = format;
        Name = name;
        MediaType = mediaType;
        IsReadOnly = isReadOnly;
    }

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public ContentFormat Format { get; }

    /// <inheritdoc/>
    public string? MediaType { get; }

    /// <inheritdoc/>
    public long? Length => _data.Length;

    /// <inheritdoc/>
    public bool IsReadOnly { get; }

    /// <inheritdoc/>
    public bool CanReopen => true;

    /// <inheritdoc/>
    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ReadOnlyMemoryStream(_data);
    }

    /// <inheritdoc/>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(OpenRead());
    }

    /// <inheritdoc/>
    public Stream OpenWrite()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReadOnly)
        {
            throw new NotSupportedException(ErrorMessages.ContentReadOnly);
        }

        return new CommitOnDisposeStream(this);
    }

    /// <inheritdoc/>
    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(OpenWrite());
    }

    /// <inheritdoc/>
    public void Dispose() => _disposed = true;

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A caller-owned read view over the current bytes; disposing it does not affect the content.</summary>
    private sealed class ReadOnlyMemoryStream : MemoryStream
    {
        /// <summary>Initializes a new instance of the <see cref="ReadOnlyMemoryStream"/> class.</summary>
        /// <param name="data">The bytes copied into the non-writable view.</param>
        public ReadOnlyMemoryStream(ReadOnlyMemory<byte> data)
            : base(data.ToArray(), writable: false)
        {
        }
    }

    /// <summary>Buffers writes and commits them to the owning content when disposed.</summary>
    private sealed class CommitOnDisposeStream : MemoryStream
    {
        private readonly MemoryContent _owner;

        /// <summary>Initializes a new instance of the <see cref="CommitOnDisposeStream"/> class.</summary>
        /// <param name="owner">The content that receives the buffered bytes when the stream is disposed.</param>
        public CommitOnDisposeStream(MemoryContent owner)
        {
            _owner = owner;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_owner._disposed)
            {
                _owner._data = ToArray();
            }

            base.Dispose(disposing);
        }
    }
}
