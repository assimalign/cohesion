using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    internal StorageTempStatement(StorageTempSpace space, long quota) { Space = space; Quota = quota; }
    /// <summary>Gets the statement's physical-byte quota.</summary>
    public long Quota { get; }

    /// <summary>Creates an append-only run owned by this statement.</summary>
    /// <param name="cancellationToken">Checked before opening a file.</param>
    /// <returns>An empty run; dispose it or the statement to delete it.</returns>
    /// <exception cref="System.IO.IOException">The run or directory lease cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory cannot be accessed.</exception>
    /// <exception cref="ObjectDisposedException">The statement or space is disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public StorageTempRun CreateRun(CancellationToken cancellationToken = default)
    {
        lock (Space.Gate)
        {
            Check(); cancellationToken.ThrowIfCancellationRequested();
            var run = new StorageTempRun(this, Space.CreateFile());
            _runs.Add(run); return run;
        }
    }
    internal void Check() => ObjectDisposedException.ThrowIf(_disposed, this);
    internal void Forget(StorageTempRun run)
    {
        if (!_disposed) { _runs.Remove(run); }
    }

    /// <summary>Deletes all owned runs and releases their quota, including partially written runs.</summary>
    /// <exception cref="AggregateException">One or more closes failed after all runs were released.</exception>
    public void Dispose()
    {
        lock (Space.Gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            // Detach before walking: Forget no longer mutates the set. Repeatedly taking its
            // first surviving entry would rescan tombstones and make parent disposal quadratic.
            List<Exception>? failures = null;
            try
            {
                foreach (var run in _runs)
                {
                    try { run.Dispose(); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { (failures ??= []).Add(error); } // Continue closing every owned run.
                }
            }
            finally { _runs.Clear(); Space.Forget(this); }
            if (failures is not null) { throw new AggregateException(ErrorMessages.TempCleanupFailed, failures); }
        }
    }
    private sealed class DebugView
    {
        private readonly StorageTempStatement _target;
        public DebugView(StorageTempStatement target) { _target = target; }
        public long Quota => _target.Quota;
    }
}
