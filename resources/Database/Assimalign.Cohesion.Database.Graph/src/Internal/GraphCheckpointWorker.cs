using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Internal;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The engine-owned checkpointer: checkpoints a database when its journal reaches
/// <see cref="GraphDatabaseEngineOptions.CheckpointJournalSize"/>, or when
/// <see cref="GraphDatabaseEngineOptions.CheckpointInterval"/> has passed since its last
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
/// <para>
/// <b>A failed checkpoint is one database's failure (#1268).</b> Any other failure — a page write
/// the checkpoint's flush could not make, a deferred checkpoint's failure the coordinator hands
/// back — is reported for that database, and the pass goes on to the next one. Later passes skip
/// the failing database for <see cref="DatabaseEngineWorker.FailureBackoff"/> and then retry it,
/// since its journal is still due, while every other database keeps being checkpointed at the
/// worker's full pace: one database's failing device never holds back another's truncation. The
/// failure stays recorded until a pass checkpoints the database; a checkpoint deferred to a running
/// statement keeps it recorded, and records nothing for a database without one. A failure that
/// took the database offline (a failed durable flush, or a header slot write that failed) is not
/// the worker's: the database is skipped from then on and the engine lists it offline. Before
/// #1268 any such exception escaped the pass and ended the worker for good.
/// </para>
/// </remarks>
internal sealed class GraphCheckpointWorker : DatabaseEngineWorker
{
    /// <summary>
    /// The longest the worker sleeps between two looks at the time backstop and at a busy storage.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly GraphDatabaseEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphCheckpointWorker"/> class.
    /// </summary>
    /// <param name="engine">The graph database engine whose open databases are checkpointed.</param>
    public GraphCheckpointWorker(GraphDatabaseEngine engine)
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
    protected override void RunIterationCore(CancellationToken cancellationToken)
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

            try
            {
                // An offline database is not begun: the engine reports it (#1243), and a failure
                // the worker recorded for it ends. A database whose failure is backing off waits.
                if (database.IsOffline || !BeginDatabase(database.Name) || !database.DataStorage.IsCheckpointDue(interval))
                {
                    continue;
                }

                // A statement holding the apply gate takes the checkpoint over and runs it as it
                // ends, so the worker never waits on one database while the others' journals grow.
                if (!database.Coordinator.TryCheckpoint(TimeSpan.Zero, cancellationToken))
                {
                    ReportUnfinished(database.Name);
                }
            }
            catch (StorageTransactionException)
            {
                // A transaction is active on this storage; retry on the next pass.
                ReportUnfinished(database.Name);
            }
            catch (ObjectDisposedException) when (!_engine.IsOpen(database))
            {
                // The snapshot can race a database drop; nothing left to checkpoint.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A failure that took the database offline (#1243, #1268) is reported through
                // the engine's offline list, and the next pass skips the database; any other is
                // this pass's failure, and the next pass retries the checkpoint.
                if (!database.IsOffline)
                {
                    ReportFailure(database.Name, exception);
                }
            }
        }
    }
}
