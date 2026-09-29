using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Storage;

using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.FileSystem;

/// <summary>
/// Provides positional page I/O and explicit durability through a file handle.
/// Legacy streams remain supported for non-durable I/O only.
/// </summary>
/// <example>
/// <code>
/// // Use a MemoryStream for in-memory storage
/// var stream = new StorageStream(new MemoryStream());
///
/// // Or use the convenience factory methods
/// var stream = StorageStream.FromFile("data.db");
/// var stream = StorageStream.FromInMemory();
/// </code>
/// </example>
public class StorageStream : Stream, IFileSystemFileHandle
{
    private readonly Stream _inner;
    private readonly IFileSystemFileHandle _handle;

    /// <summary>
    /// Initializes a non-durable adapter over a stream. Even a physical stream must
    /// be opened through a file handle to carry an explicit durability contract.
    /// </summary>
    /// <param name="innerStream">The backing stream. Must support read, write, and seek operations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="innerStream"/> is <c>null</c>.</exception>
    public StorageStream(Stream innerStream)
    {
        ArgumentNullException.ThrowIfNull(innerStream);
        _inner = innerStream;
        _handle = new StreamFileHandle(innerStream);
    }

    /// <summary>Initializes storage over an explicitly durability-aware handle.</summary>
    /// <param name="handle">The owned backing handle.</param>
    public StorageStream(IFileSystemFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
        _inner = new FileHandleStream(handle);
    }

    /// <summary>Wraps storage while retaining its explicit handle contract.</summary>
    /// <param name="stream">The owned backing storage stream.</param>
    public StorageStream(StorageStream stream) : this((IFileSystemFileHandle)stream) { }

    /// <inheritdoc />
    public bool SupportsDurableFlush => _handle.SupportsDurableFlush;

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
    public override void Flush() => Flush(durable: false);

    /// <summary>
    /// Flushes to durable storage, or throws when the handle cannot provide durability.
    /// </summary>
    public void FlushDurable() => Flush(durable: true);

    /// <inheritdoc />
    public void Flush(bool durable)
    {
        if (durable && !SupportsDurableFlush)
        {
            throw new NotSupportedException("The backing handle cannot provide a durable flush.");
        }
        _handle.Flush(durable);
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => FlushAsync(false, cancellationToken).AsTask();

    /// <inheritdoc />
    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        if (durable && !SupportsDurableFlush)
        {
            throw new NotSupportedException("The backing handle cannot provide a durable flush.");
        }
        return _handle.FlushAsync(durable, cancellationToken);
    }

    /// <inheritdoc />
    public int Read(Span<byte> buffer, long offset) => _handle.Read(buffer, offset);

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        => _handle.ReadAsync(buffer, offset, cancellationToken);

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> buffer, long offset) => _handle.Write(buffer, offset);

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        => _handle.WriteAsync(buffer, offset, cancellationToken);

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
        int totalRead = 0;
        while (totalRead < Page.Size)
        {
            int bytesRead = _handle.Read(buffer.AsSpan(totalRead, Page.Size - totalRead), offset + totalRead);
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
        _handle.Write(buffer.AsSpan(0, Page.Size), offset);
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
        int totalRead = 0;
        while (totalRead < Page.HeaderSize)
        {
            int bytesRead = _handle.Read(buffer[totalRead..Page.HeaderSize], offset + totalRead);
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
        int totalRead = 0;
        while (totalRead < Page.Size)
        {
            int bytesRead = await _handle.ReadAsync(buffer.Slice(totalRead, Page.Size - totalRead), offset + totalRead, cancellationToken);
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
        await _handle.WriteAsync(buffer[..Page.Size], offset, cancellationToken);
    }

    /// <summary>
    /// Creates a <see cref="StorageStream"/> backed by a file on disk.
    /// </summary>
    /// <param name="path">The file path to open or create.</param>
    /// <returns>A new <see cref="StorageStream"/> wrapping the file.</returns>
    public static StorageStream FromFile(string path)
    {
        return new StorageStream(StorageFileSystem.OpenHandle(path, null, FileShare.None));
    }

    /// <summary>Opens physical storage with an explicit creation and sharing policy.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="mode">Open, OpenOrCreate, Create, or CreateNew.</param>
    /// <param name="share">Access permitted to other handles.</param>
    /// <returns>A storage stream retaining the physical handle's durability contract.</returns>
    public static StorageStream FromFile(string path, FileMode mode, FileShare share)
        => new(StorageFileSystem.OpenHandle(path, null, share, mode));

    /// <summary>Opens storage through the supplied file system.</summary>
    /// <param name="path">A path understood by the supplied file system.</param>
    /// <param name="fileSystem">The file system, whose lifetime remains owned by the caller.</param>
    /// <returns>A new storage stream owning the opened handle.</returns>
    public static StorageStream FromFile(string path, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return new StorageStream(StorageFileSystem.OpenHandle(path, fileSystem, FileShare.None));
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
