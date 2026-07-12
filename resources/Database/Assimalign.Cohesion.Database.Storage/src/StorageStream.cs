using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Wraps any <see cref="Stream"/> to provide page-aligned I/O for storage files.
/// Supports any seekable, readable, and writable stream including
/// <see cref="FileStream"/>, <see cref="MemoryStream"/>, or custom implementations.
/// </summary>
/// <example>
/// <code>
/// // Use a MemoryStream for in-memory storage
/// var stream = new StorageStream(new MemoryStream());
///
/// // Use a FileStream for disk-backed storage
/// var stream = new StorageStream(new FileStream("data.db", FileMode.OpenOrCreate, FileAccess.ReadWrite));
///
/// // Or use the convenience factory methods
/// var stream = StorageStream.FromFile("data.db");
/// var stream = StorageStream.FromInMemory();
/// </code>
/// </example>
public class StorageStream : Stream
{
    private readonly Stream _inner;

    /// <summary>
    /// Initializes a new <see cref="StorageStream"/> that delegates all I/O to the specified inner stream.
    /// </summary>
    /// <param name="innerStream">The backing stream. Must support read, write, and seek operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="innerStream"/> is <c>null</c>.</exception>
    public StorageStream(Stream innerStream)
    {
        ArgumentNullException.ThrowIfNull(innerStream);
        _inner = innerStream;
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.WriteAsync(buffer, cancellationToken);

    /// <summary>
    /// Reads a full page from the stream at the byte position calculated from the page identifier.
    /// </summary>
    /// <param name="pageId">The zero-based page identifier.</param>
    /// <param name="buffer">A buffer of at least <see cref="Page.Size"/> bytes to read into.</param>
    /// <exception cref="StorageIOException">The stream returned fewer bytes than a full page.</exception>
    public void ReadPage(PageId pageId, byte[] buffer)
    {
        long offset = (long)pageId * Page.Size;
        _inner.Seek(offset, SeekOrigin.Begin);

        int totalRead = 0;
        while (totalRead < Page.Size)
        {
            int bytesRead = _inner.Read(buffer, totalRead, Page.Size - totalRead);
            if (bytesRead == 0)
            {
                throw new StorageIOException($"Unexpected end of stream reading page {(long)pageId}.");
            }
            totalRead += bytesRead;
        }
    }

    /// <summary>
    /// Writes a full page to the stream at the byte position calculated from the page identifier.
    /// </summary>
    /// <param name="pageId">The zero-based page identifier.</param>
    /// <param name="buffer">A buffer of at least <see cref="Page.Size"/> bytes to write from.</param>
    public void WritePage(PageId pageId, byte[] buffer)
    {
        long offset = (long)pageId * Page.Size;
        _inner.Seek(offset, SeekOrigin.Begin);
        _inner.Write(buffer, 0, Page.Size);
    }

    /// <summary>
    /// Reads only the page header from the stream at the byte position calculated from
    /// the page identifier. Used to reconstruct storage state without loading full pages.
    /// </summary>
    /// <param name="pageId">The zero-based page identifier.</param>
    /// <param name="buffer">A buffer of at least <see cref="Page.HeaderSize"/> bytes to read into.</param>
    /// <exception cref="StorageIOException">The stream returned fewer bytes than a full page header.</exception>
    public void ReadPageHeader(PageId pageId, Span<byte> buffer)
    {
        long offset = (long)pageId * Page.Size;
        _inner.Seek(offset, SeekOrigin.Begin);

        int totalRead = 0;
        while (totalRead < Page.HeaderSize)
        {
            int bytesRead = _inner.Read(buffer[totalRead..Page.HeaderSize]);
            if (bytesRead == 0)
            {
                throw new StorageIOException($"Unexpected end of stream reading the header of page {(long)pageId}.");
            }
            totalRead += bytesRead;
        }
    }

    /// <summary>
    /// Reads a full page from the stream asynchronously.
    /// </summary>
    /// <param name="pageId">The zero-based page identifier.</param>
    /// <param name="buffer">A memory buffer of at least <see cref="Page.Size"/> bytes.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous read operation.</returns>
    public async ValueTask ReadPageAsync(PageId pageId, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long offset = (long)pageId * Page.Size;
        _inner.Seek(offset, SeekOrigin.Begin);

        int totalRead = 0;
        while (totalRead < Page.Size)
        {
            int bytesRead = await _inner.ReadAsync(buffer.Slice(totalRead, Page.Size - totalRead), cancellationToken);
            if (bytesRead == 0)
            {
                throw new StorageIOException($"Unexpected end of stream reading page {(long)pageId}.");
            }
            totalRead += bytesRead;
        }
    }

    /// <summary>
    /// Writes a full page to the stream asynchronously.
    /// </summary>
    /// <param name="pageId">The zero-based page identifier.</param>
    /// <param name="buffer">A memory buffer of at least <see cref="Page.Size"/> bytes.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    public async ValueTask WritePageAsync(PageId pageId, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        long offset = (long)pageId * Page.Size;
        _inner.Seek(offset, SeekOrigin.Begin);
        await _inner.WriteAsync(buffer[..Page.Size], cancellationToken);
    }

    /// <summary>
    /// Creates a <see cref="StorageStream"/> backed by a file on disk.
    /// </summary>
    /// <param name="path">The file path to open or create.</param>
    /// <returns>A new <see cref="StorageStream"/> wrapping the file.</returns>
    public static StorageStream FromFile(string path)
    {
        return new StorageStream(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    /// <summary>
    /// Creates a <see cref="StorageStream"/> backed by an in-memory buffer.
    /// </summary>
    /// <returns>A new <see cref="StorageStream"/> wrapping a <see cref="MemoryStream"/>.</returns>
    public static StorageStream FromInMemory()
    {
        return new StorageStream(new MemoryStream());
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
