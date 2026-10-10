using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// A pass-through file handle whose flush fails with an <see cref="IOException"/> once a write
/// at a chosen offset reached the inner handle, or on demand: an fsync that reports failure for a
/// write the device may already hold. A write at a chosen offset, or the next write, can fail
/// before it writes anything, and a write at a chosen offset can first run an action. Everything
/// else goes straight to the inner handle.
/// </summary>
public sealed class FlushFaultingHandle : IFileSystemFileHandle
{
    private readonly IFileSystemFileHandle _inner;
    private bool _failNextFlush;
    private bool _failNextWrite;

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
    /// Gets or sets an action run once, before anything else, when a write at its offset arrives:
    /// what another thread does while that write is in flight. Null runs none.
    /// </summary>
    public (long Offset, Action Action)? OnWriteAt { get; set; }

    /// <summary>
    /// Arms the next write, at any offset, to fail before writing anything.
    /// </summary>
    public void FailNextWrite() => _failNextWrite = true;

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

    /// <summary>
    /// Gets or sets whether the handle reports that it cannot flush durably, as a provider whose
    /// contract rejects a durable flush does (#1018); the storage stream then refuses every durable
    /// flush before it reaches the handle.
    /// </summary>
    public bool RefuseDurableFlush { get; set; }

    /// <inheritdoc />
    public bool SupportsDurableFlush => !RefuseDurableFlush && _inner.SupportsDurableFlush;

    /// <inheritdoc />
    public long Length => _inner.Length;

    /// <summary>
    /// Gets or sets the exception the next durable flush throws instead of flushing, whatever the
    /// handle reports through <see cref="SupportsDurableFlush"/>: a handle that stops supporting a
    /// durable flush after the storage checked it. Null throws none.
    /// </summary>
    public Exception? NextDurableFlushFailure { get; set; }

    /// <inheritdoc />
    public void Flush(bool durable = false)
    {
        if (durable && NextDurableFlushFailure is { } failure)
        {
            NextDurableFlushFailure = null;
            FailedFlushes++;
            throw failure;
        }

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
        if (OnWriteAt is { } hook && hook.Offset == offset)
        {
            OnWriteAt = null;
            hook.Action();
        }

        if (_failNextWrite)
        {
            _failNextWrite = false;
            throw new IOException("Injected write failure before any byte was written.");
        }

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
