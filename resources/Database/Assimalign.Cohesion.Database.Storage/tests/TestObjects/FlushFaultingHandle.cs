using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// A pass-through file handle whose flush fails with an <see cref="IOException"/> once a write
/// at a chosen offset reached the inner handle, or on demand: an fsync that reports failure for a
/// write the device may already hold. A write at a chosen offset can fail before it writes
/// anything. Everything else goes straight to the inner handle.
/// </summary>
public sealed class FlushFaultingHandle : IFileSystemFileHandle
{
    private readonly IFileSystemFileHandle _inner;
    private bool _failNextFlush;

    /// <summary>
    /// Initializes a handle over <paramref name="inner"/>.
    /// </summary>
    /// <param name="inner">The handle every operation goes to.</param>
    public FlushFaultingHandle(IFileSystemFileHandle inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Gets or sets the offset whose next write arms one failing flush; null arms none.
    /// </summary>
    public long? FailFlushAfterWriteAt { get; set; }

    /// <summary>
    /// Gets or sets the offset whose next write fails before writing anything; null fails none.
    /// </summary>
    public long? FailWriteAt { get; set; }

    /// <summary>
    /// Gets the number of flushes that succeeded, durable or not.
    /// </summary>
    public int Flushes { get; private set; }

    /// <summary>
    /// Arms the next flush to fail, whatever was written before it.
    /// </summary>
    public void FailNextFlush() => _failNextFlush = true;

    /// <summary>
    /// Gets the number of flushes that failed.
    /// </summary>
    public int FailedFlushes { get; private set; }

    /// <inheritdoc />
    public bool SupportsDurableFlush => _inner.SupportsDurableFlush;

    /// <inheritdoc />
    public long Length => _inner.Length;

    /// <inheritdoc />
    public void Flush(bool durable = false)
    {
        // Only a durable flush fails: an fsync, not the ordinary flush that hands bytes to the
        // operating system.
        if (_failNextFlush && durable)
        {
            _failNextFlush = false;
            FailedFlushes++;
            throw new IOException("Injected flush failure after a write reached the device.");
        }

        _inner.Flush(durable);
        Flushes++;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush(durable);
        return default;
    }

    /// <inheritdoc />
    public int Read(Span<byte> buffer, long offset) => _inner.Read(buffer, offset);

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, offset, cancellationToken);

    /// <inheritdoc />
    public void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc />
    public void Write(ReadOnlySpan<byte> buffer, long offset)
    {
        if (FailWriteAt == offset)
        {
            FailWriteAt = null;
            throw new IOException("Injected write failure before any byte was written.");
        }

        _inner.Write(buffer, offset);

        if (FailFlushAfterWriteAt == offset)
        {
            FailFlushAfterWriteAt = null;
            _failNextFlush = true;
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span, offset);
        return default;
    }

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
