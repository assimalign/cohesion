using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>Bounded append-only hash partition files for future grace joins and aggregates.</summary>
[DebuggerDisplay("StorageTempPartitions {PartitionCount}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class StorageTempPartitions : IDisposable
{
    private readonly StorageTempStatement _statement;
    private readonly StorageTempRun?[] _runs;
    private bool _sealed;
    private bool _disposed;
    private StorageTempPartitions(StorageTempStatement statement, int count) { _statement = statement; _runs = new StorageTempRun?[count]; }

    /// <summary>Creates partitions with at most one private writer page per partition.</summary>
    /// <param name="statement">Owner of the partition files.</param>
    /// <param name="partitionCount">Positive number of hash partitions.</param>
    /// <param name="memoryGrant">At least partitionCount times (8192 + 512) bytes, including bookkeeping.</param>
    /// <returns>An empty partition set; files are created lazily.</returns>
    /// <exception cref="ArgumentNullException">The statement is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The count is nonpositive or the grant is too small.</exception>
    /// <exception cref="ObjectDisposedException">The statement or its space is disposed.</exception>
    public static StorageTempPartitions Create(StorageTempStatement statement, int partitionCount, long memoryGrant)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryGrant, partitionCount * (StorageTempSpace.PageSize + 512L));
        statement.Check();
        return new StorageTempPartitions(statement, partitionCount);
    }
    /// <summary>Gets the number of partitions.</summary>
    public int PartitionCount => _runs.Length;
    /// <summary>Appends a record to hash modulo PartitionCount.</summary>
    /// <param name="hash">A caller-computed hash; equal join/group keys must supply equal hashes.</param>
    /// <param name="record">Opaque bytes copied before returning.</param>
    /// <param name="cancellationToken">Cancels creation or page writes.</param>
    /// <exception cref="InvalidOperationException">The partitions are sealed.</exception>
    /// <exception cref="ObjectDisposedException">The partitions are disposed.</exception>
    /// <exception cref="StorageTempException">A quota or floor refused growth, or temporary I/O failed (CK05005).</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public void Append(ulong hash, ReadOnlySpan<byte> record, CancellationToken cancellationToken = default)
    {
        AppendCore(hash, record, cancellationToken);
    }

    private void AppendCore(ulong hash, ReadOnlySpan<byte> record, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _statement.Check();
        if (_sealed) { throw new InvalidOperationException(ErrorMessages.TempRunSealed); }
        try
        {
            int partition = (int)(hash % (ulong)_runs.Length);
            var run = _runs[partition] ??= _statement.CreateRun(cancellationToken);
            run.Append(record, cancellationToken);
        }
        catch { Dispose(); throw; }
    }
    /// <summary>Seals all files; empty partitions remain absent.</summary>
    /// <exception cref="StorageTempException">Temporary I/O failed (CK05005).</exception>
    /// <param name="cancellationToken">Cancels before each file's tail flush.</param>
    /// <exception cref="ObjectDisposedException">The partitions are disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public void Seal(CancellationToken cancellationToken = default)
    {
        SealCore(cancellationToken);
    }

    private void SealCore(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _statement.Check();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var run in _runs) { run?.Seal(cancellationToken); }
            _sealed = true;
        }
        catch { Dispose(); throw; }
    }
    /// <summary>Gets a sealed partition run, or null for an empty partition. This set retains ownership.</summary>
    /// <param name="partition">The zero-based partition ordinal.</param>
    /// <returns>The sealed run, or null.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is invalid.</exception>
    /// <exception cref="InvalidOperationException">Seal has not run.</exception>
    /// <exception cref="ObjectDisposedException">The partitions are disposed.</exception>
    public StorageTempRun? GetPartition(int partition)
    {
        return GetPartitionCore(partition);
    }

    private StorageTempRun? GetPartitionCore(int partition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _statement.Check();
        ArgumentOutOfRangeException.ThrowIfNegative(partition);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(partition, _runs.Length);
        if (!_sealed) { throw new InvalidOperationException(ErrorMessages.TempRunUnsealed); }
        return _runs[partition];
    }
    /// <summary>Deletes every partition file and releases its quota.</summary>
    /// <exception cref="AggregateException">One or more coded closes failed after all partitions were released.</exception>
    public void Dispose()
    {
        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposed) { return; }
        _disposed = true;
        List<Exception>? failures = null;
        foreach (var run in _runs)
        {
            try { run?.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { (failures ??= []).Add(error); } // Release all partitions after a failed close.
        }
        Array.Clear(_runs);
        if (failures is not null) { throw new AggregateException(ErrorMessages.TempCleanupFailed, failures); }
    }
    private sealed class DebugView
    {
        private readonly StorageTempPartitions _target;
        public DebugView(StorageTempPartitions target) { _target = target; }
        public int PartitionCount => _target.PartitionCount;
    }
}
