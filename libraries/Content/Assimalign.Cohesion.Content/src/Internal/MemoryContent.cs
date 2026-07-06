using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// In-memory content. Always reopenable; writable unless created read-only. Writes are committed when
/// the write stream is disposed, atomically replacing the visible bytes.
/// </summary>
internal sealed class MemoryContent : IWritableContent
{
    private ReadOnlyMemory<byte> _data;
    private bool _disposed;

    internal MemoryContent(ReadOnlyMemory<byte> data, ContentFormat format, string? name, string? mediaType, bool isReadOnly)
    {
        _data = data;
        Format = format;
        Name = name;
        MediaType = mediaType;
        IsReadOnly = isReadOnly;
    }

    public string? Name { get; }

    public ContentFormat Format { get; }

    public string? MediaType { get; }

    public long? Length => _data.Length;

    public bool IsReadOnly { get; }

    public bool CanReopen => true;

    public Stream OpenRead()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ReadOnlyMemoryStream(_data);
    }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(OpenRead());
    }

    public Stream OpenWrite()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsReadOnly)
        {
            throw new NotSupportedException("The content is read-only.");
        }

        return new CommitOnDisposeStream(this);
    }

    public ValueTask<Stream> OpenWriteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Stream>(OpenWrite());
    }

    public void Dispose() => _disposed = true;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A caller-owned read view over the current bytes; disposing it does not affect the content.</summary>
    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> data) : MemoryStream(data.ToArray(), writable: false);

    /// <summary>Buffers writes and commits them to the owning content when disposed.</summary>
    private sealed class CommitOnDisposeStream(MemoryContent owner) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing && !owner._disposed)
            {
                owner._data = ToArray();
            }

            base.Dispose(disposing);
        }
    }
}
