using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// A random-access handle to a file. Mirrors the shape of <see cref="System.IO.RandomAccess"/>
/// over a <see cref="Microsoft.Win32.SafeHandles.SafeFileHandle"/>, which is what a page-oriented
/// storage engine actually needs: offset-addressed I/O that is safe to issue concurrently, plus a
/// flush whose durability is knowable rather than assumed.
/// </summary>
public interface IFileSystemFileHandle : IDisposable, IAsyncDisposable
{
    /// <summary>The current length of the file, in bytes.</summary>
    long Length { get; }

    /// <summary>
    /// Whether <see cref="Flush(bool)"/> with <c>durable: true</c> can actually guarantee the data
    /// has reached durable storage. An in-memory or otherwise non-durable file system returns
    /// <see langword="false"/> rather than pretending.
    /// </summary>
    bool SupportsDurableFlush { get; }

    /// <summary>Reads into <paramref name="buffer"/> starting at <paramref name="offset"/>.</summary>
    /// <returns>The number of bytes read, which may be fewer than requested at end of file.</returns>
    int Read(Span<byte> buffer, long offset);

    /// <inheritdoc cref="Read(Span{byte}, long)"/>
    ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default);

    /// <summary>Writes <paramref name="buffer"/> starting at <paramref name="offset"/>, extending the file if needed.</summary>
    void Write(ReadOnlySpan<byte> buffer, long offset);

    /// <inheritdoc cref="Write(ReadOnlySpan{byte}, long)"/>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default);

    /// <summary>Sets the file length, truncating or extending it.</summary>
    void SetLength(long length);

    /// <summary>
    /// Flushes buffered writes. When <paramref name="durable"/> is <see langword="true"/> the data
    /// must have reached durable storage when this returns.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// <paramref name="durable"/> is <see langword="true"/> and <see cref="SupportsDurableFlush"/>
    /// is <see langword="false"/>. Failing loudly is deliberate: a storage engine that asks for
    /// durability and silently does not get it is worse than one that cannot start.
    /// </exception>
    void Flush(bool durable);

    /// <inheritdoc cref="Flush(bool)"/>
    ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default);
}
