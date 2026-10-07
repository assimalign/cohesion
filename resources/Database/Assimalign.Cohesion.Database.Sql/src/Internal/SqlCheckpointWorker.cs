using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The engine-owned checkpointer: checkpoints a database's file set — the data set and the
/// <c>.catalog</c> set, each on its own — when its journal reaches
/// <see cref="SqlDatabaseEngineOptions.CheckpointJournalSize"/>, or when
/// <see cref="SqlDatabaseEngineOptions.CheckpointInterval"/> has passed since its last checkpoint
/// and its journal received records since (#1254). A checkpoint durably flushes all page state
/// and truncates the journal with continued LSNs (LSNs never restart across truncation).
/// </summary>
/// <remarks>
/// The pass, its lanes and its failure handling are the engines' shared checkpointer
/// (<see cref="DatabaseCheckpointWorker{TDatabase}"/>): each database's checkpoint runs on a lane of
/// its own, so a checkpoint that hangs or crawls in its device holds back its own database only,
/// and a failed checkpoint is that database's failure (#1268). A SQL database's checkpoint covers
/// both of its file sets on its lane: the data set through the transaction coordinator, which
/// defers the checkpoint to a statement holding the apply gate, and the catalog set directly.
/// Before #1268 any failure escaped the pass and ended the worker for good. A database its holder
/// closed while the engine keeps it registered is never due, and its checkpoint racing the close is
/// not a failure (<see cref="SqlDatabaseEngine.IsOpen(SqlDatabase)"/> is false for it).
/// </remarks>
internal sealed class SqlCheckpointWorker : DatabaseCheckpointWorker<SqlDatabase>
{
    private readonly SqlDatabaseEngine _engine;

    internal SqlCheckpointWorker(SqlDatabaseEngine engine)
        : base(engine.Name, engine.EngineOptions.CheckpointInterval, engine.JournalSizeLimit)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    protected override long GetJournalLength(SqlDatabase database)
        => Math.Max(database.DataStorage.JournalLength, database.CatalogStorage.JournalLength);

    /// <inheritdoc />
    protected override ManualResetEventSlim CheckpointNeededSignal => _engine.CheckpointNeededSignal;

    /// <inheritdoc />
    protected override SqlDatabase[] GetDatabases() => _engine.GetInstanceSnapshot();

    /// <inheritdoc />
    protected override string GetName(SqlDatabase database) => database.Name;

    /// <inheritdoc />
    protected override bool IsOffline(SqlDatabase database) => database.IsOffline;

    /// <inheritdoc />
    protected override bool IsOpen(SqlDatabase database) => _engine.IsOpen(database);

    /// <inheritdoc />
    /// <remarks>
    /// A database its holder closed is never due: the engine keeps it registered until its close ends,
    /// then forgets it, and its disposed storages have nothing left to checkpoint. A close that was not idle
    /// (a writer the close kept in flight, #1226) leaves the data journal untruncated, so without
    /// this the closed data set would stay due for a checkpoint the storage refuses with
    /// <c>StorageTransactionException</c>, which <see cref="IsOpen"/> does not cover, and a failure
    /// recorded for the database would never end.
    /// </remarks>
    protected override bool IsCheckpointDue(SqlDatabase database, TimeSpan interval)
        => !database.IsClosed && (database.DataStorage.IsCheckpointDue(interval) || database.CatalogStorage.IsCheckpointDue(interval));

    /// <inheritdoc />
    protected override bool Checkpoint(SqlDatabase database, TimeSpan interval, CancellationToken cancellationToken)
    {
        bool dataDue = database.DataStorage.IsCheckpointDue(interval);
        bool catalogDue = database.CatalogStorage.IsCheckpointDue(interval);
        if (!dataDue && !catalogDue)
        {
            return true;
        }

        // Re-export the index registrations first when they changed — a backstop: root pages stay
        // fixed through splits (#1159), and the checkpoint is the engine's persistence point for
        // re-attachment metadata.
        database.SaveIndexRegistrationsIfChanged();

        // The data storage checkpoints through the transaction coordinator: the truncating
        // checkpoint record carries every in-flight logical transaction's sequence, so recovery
        // classification survives the truncation. A statement holding the apply gate takes the
        // checkpoint over and runs it as it ends. The catalog storage has no logical transactions
        // above it and checkpoints directly.
        bool finished = !dataDue || database.TryCheckpointDataStorage(cancellationToken);

        if (catalogDue || database.CatalogStorage.IsCheckpointDue(interval))
        {
            database.CatalogStorage.Checkpoint();
        }

        return finished;
    }
}
