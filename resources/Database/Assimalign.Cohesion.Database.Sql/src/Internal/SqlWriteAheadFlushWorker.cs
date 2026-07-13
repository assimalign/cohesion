using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The engine-owned write-ahead-log group-commit flusher: woken when a grouped
/// commit registers on any open storage's durability gate, it performs one durable
/// flush per storage that covers every pending commit, so concurrent commits share a
/// single fsync.
/// </summary>
/// <remarks>
/// Signal-driven: <see cref="WaitForTrigger"/> waits on the engine's commit-pending
/// signal (every open storage's <c>OnCommitPending</c> sets it) bounded by the
/// group-commit window. The signal is reset <em>before</em> the flush pass, so a
/// commit arriving mid-pass re-sets it and is served by the next pass. In the
/// synchronous durability mode there is never anything pending and the worker idles.
/// </remarks>
internal sealed class SqlWriteAheadFlushWorker : DatabaseEngineWorker
{
    private readonly SqlDatabaseEngine _engine;
    private readonly ManualResetEventSlim _commitPending;

    internal SqlWriteAheadFlushWorker(SqlDatabaseEngine engine, ManualResetEventSlim commitPending)
    {
        _engine = engine;
        _commitPending = commitPending;
    }

    /// <inheritdoc />
    public override string Name => _engine.Name + "/wal-flush";

    /// <inheritdoc />
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.WriteAheadFlush;

    /// <inheritdoc />
    public override TimeSpan Interval
        => _engine.EngineOptions.Durability == StorageCommitDurability.Grouped
            ? _engine.EngineOptions.GroupCommitWindow
            : TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    protected override void WaitForTrigger(CancellationToken cancellationToken)
    {
        try
        {
            _commitPending.Wait(Interval, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The pump is stopping; Run observes the token and exits.
        }
    }

    /// <inheritdoc />
    public override void RunIteration(CancellationToken cancellationToken)
    {
        // Reset before flushing: a commit that registers mid-pass sets the signal
        // again and is picked up by the next pass instead of being lost.
        _commitPending.Reset();

        foreach (SqlStorage storage in _engine.GetStorageSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                storage.FlushPendingCommits();
            }
            catch (ObjectDisposedException)
            {
                // The snapshot can race a database drop; a disposed storage has no
                // committers left to serve.
            }
        }
    }
}
