using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>Owns and accounts all temporary runs of one statement.</summary>
[DebuggerDisplay("StorageTempStatement {Quota}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class StorageTempStatement : IDisposable
{
    private readonly HashSet<StorageTempRun> _runs = [];
    private bool _disposed;
    internal StorageTempSpace Space { get; }
    internal long Bytes { get; set; }
    internal long Peak { get; set; }
    internal StorageTempStatement(StorageTempSpace space, long quota) { Space = space; Quota = quota; }
    /// <summary>Gets the statement's physical-byte quota.</summary>
    public long Quota { get; }

    /// <summary>Gets the currently reserved physical bytes, including unwritten tails.</summary>
    public long PhysicalBytes { get { lock (Space.Gate) { return Bytes; } } }
    /// <summary>Gets the largest simultaneous physical-byte reservation in this lifetime.</summary>
    public long PeakBytes { get { lock (Space.Gate) { return Peak; } } }

    /// <summary>Creates an append-only run owned by this statement.</summary>
    /// <param name="cancellationToken">Checked before opening a file.</param>
    /// <returns>An empty run; dispose it or the statement to delete it.</returns>
    /// <exception cref="StorageTempException">Temporary storage is unavailable (CK05005), with the I/O cause.</exception>
    /// <exception cref="ObjectDisposedException">The statement or space is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public StorageTempRun CreateRun(CancellationToken cancellationToken = default)
    {
        Check(); cancellationToken.ThrowIfCancellationRequested();
        var file = Space.CreateFile();
        StorageTempRun run;
        try { run = new StorageTempRun(this, file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { file.Dispose(); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { _ = StorageTempException.Unavailable(cleanup); } // Count cleanup without replacing the cause.
            throw StorageTempException.Unavailable(error);
        }
        catch
        {
            try { file.Dispose(); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { _ = StorageTempException.Unavailable(cleanup); }
            throw;
        }
        try
        {
            lock (Space.Gate) { Check(); _runs.Add(run); }
            return run;
        }
        catch { run.DisposeAfterFailure(); throw; }
    }
    internal void Check() { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this); Space.Check(); }
    internal void Forget(StorageTempRun run)
    {
        lock (Space.Gate) { _runs.Remove(run); }
    }

    /// <summary>Deletes all owned runs and releases their quota, including partially written runs.</summary>
    /// <exception cref="AggregateException">One or more closes failed after all runs were released.</exception>
    public void Dispose()
    {
        StorageTempRun[] runs;
        lock (Space.Gate)
        {
            if (_disposed) { return; }
            _disposed = true; runs = [.. _runs]; _runs.Clear(); Space.Forget(this);
        }
        List<Exception>? failures = null;
        foreach (var run in runs)
        {
            try { run.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { (failures ??= []).Add(error); } // Continue closing every owned run.
        }
        if (failures is not null) { throw new AggregateException(ErrorMessages.TempCleanupFailed, failures); }
    }
    private sealed class DebugView
    {
        private readonly StorageTempStatement _target;
        public DebugView(StorageTempStatement target) { _target = target; }
        public long Quota => _target.Quota;
    }
}
