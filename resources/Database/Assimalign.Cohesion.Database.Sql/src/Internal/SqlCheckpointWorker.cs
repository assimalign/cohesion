using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// The engine-owned checkpointer: checkpoints a database's file set — the data set and the
/// <c>.catalog</c> set, each on its own — when its journal reaches
/// <see cref="SqlDatabaseEngineOptions.CheckpointJournalSize"/>, or when
/// <see cref="SqlDatabaseEngineOptions.CheckpointInterval"/> has passed since its last checkpoint
/// and its journal received records since (#1254). A checkpoint durably flushes all page state
/// and truncates the journal with continued LSNs (LSNs never restart across truncation).
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
/// The data set checkpoints through the transaction coordinator, which waits for its statement
/// apply gate, so a sustained statement load cannot keep the checkpoint out. A storage still
/// busy (<see cref="StorageTransactionException"/>) is retried at the next poll. An offline
/// database (#1243) is skipped: nothing may be written to it until it is reopened.
/// </para>
/// </remarks>
internal sealed class SqlCheckpointWorker : DatabaseEngineWorker
{
    /// <summary>
    /// The longest the worker sleeps between two looks at the time backstop and at a busy storage.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly SqlDatabaseEngine _engine;

    internal SqlCheckpointWorker(SqlDatabaseEngine engine)
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

        foreach (SqlDatabaseInstance database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (database.IsOffline)
            {
                continue;
            }

            try
            {
                bool dataDue = database.DataStorage.IsCheckpointDue(interval);
                bool catalogDue = database.CatalogStorage.IsCheckpointDue(interval);
                if (!dataDue && !catalogDue)
                {
                    continue;
                }

                // Re-export the index registrations first when they changed —
                // a backstop: root pages stay fixed through splits (#1159), and
                // the checkpoint pass is the engine's persistence point for
                // re-attachment metadata.
                database.SaveIndexRegistrationsIfChanged();

                // The data storage checkpoints through the transaction
                // coordinator: the truncating checkpoint record carries every
                // in-flight logical transaction's sequence, so recovery
                // classification survives the truncation. The catalog storage
                // has no logical transactions above it and checkpoints directly.
                if (dataDue)
                {
                    database.CheckpointDataStorage(cancellationToken);
                }

                if (catalogDue || database.CatalogStorage.IsCheckpointDue(interval))
                {
                    database.CatalogStorage.Checkpoint();
                }
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
