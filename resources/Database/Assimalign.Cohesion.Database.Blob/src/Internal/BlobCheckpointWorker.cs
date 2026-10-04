using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Blob.Internal;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The engine-owned checkpointer: checkpoints a database when its journal reaches
/// <see cref="BlobDatabaseEngineOptions.CheckpointJournalSize"/>, or when
/// <see cref="BlobDatabaseEngineOptions.CheckpointInterval"/> has passed since its last
/// checkpoint and its journal received records since (#1254). A checkpoint flushes all page
/// state by the database's durability policy and truncates the journal with continued LSNs.
/// </summary>
/// <remarks>
/// <para>
/// A storage whose journal reaches the size wakes the worker at once through the engine's
/// checkpoint signal, as PostgreSQL's <c>XLogWrite</c> requests a checkpoint once
/// <c>max_wal_size</c> is consumed (<c>src/backend/access/transam/xlog.c:2579-2584</c>); otherwise
/// the worker polls at most once a second for the time backstop, PostgreSQL's
/// <c>checkpoint_timeout</c> (<c>src/backend/postmaster/checkpointer.c:405-412</c>).
/// </para>
/// <para>
/// The checkpoint runs through the transaction coordinator, under its statement apply gate, so a
/// sustained statement load cannot keep the checkpoint out. The worker never waits for the gate:
/// when a statement holds it, the coordinator defers the checkpoint to that statement's end
/// (<c>TransactionCoordinator.TryCheckpoint</c>), so one database's long statement does not stall
/// the checkpoints of the engine's other databases. A storage still busy
/// (<see cref="StorageTransactionException"/>) is retried at the next poll. An offline database
/// (#1243) is skipped: nothing may be written to it until it is reopened.
/// </para>
/// </remarks>
internal sealed class BlobCheckpointWorker : DatabaseEngineWorker
{
    /// <summary>
    /// The longest the worker sleeps between two looks at the time backstop and at a busy storage.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly BlobDatabaseEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobCheckpointWorker"/> class.
    /// </summary>
    /// <param name="engine">The blob database engine whose open databases are checkpointed.</param>
    public BlobCheckpointWorker(BlobDatabaseEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public override string Name => _engine.Name + "/checkpoint";

    /// <inheritdoc />
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.Checkpoint;

    /// <inheritdoc />
    public override TimeSpan Interval => _engine.EngineOptions.CheckpointInterval;

    /// <inheritdoc />
    protected override void WaitForTrigger(CancellationToken cancellationToken)
    {
        var wait = Interval < PollInterval ? Interval : PollInterval;

        try
        {
            _engine.CheckpointNeededSignal.Wait(wait, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The pump is stopping; Run observes the token and exits.
        }
        catch (ObjectDisposedException)
        {
            // The engine is closing.
        }
    }

    /// <inheritdoc />
    public override void RunIteration(CancellationToken cancellationToken)
    {
        // Reset before the pass: a journal that reaches the size mid-pass sets it again.
        _engine.CheckpointNeededSignal.Reset();
        var interval = Interval;

        foreach (var database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (database.IsOffline || !database.DataStorage.IsCheckpointDue(interval))
            {
                continue;
            }

            try
            {
                // A statement holding the apply gate takes the checkpoint over and runs it as it
                // ends, so the worker never waits on one database while the others' journals grow.
                database.Coordinator.TryCheckpoint(TimeSpan.Zero, cancellationToken);
            }
            catch (StorageTransactionException)
            {
                // A transaction is active on this storage; retry on the next pass.
            }
            catch (StorageOfflineException)
            {
                // A durable flush failed and took the database offline (#1243); the next pass
                // skips it, and only a reopen brings it back.
            }
            catch (ObjectDisposedException)
            {
                // The snapshot can race a database drop; nothing left to checkpoint.
            }
        }
    }
}
