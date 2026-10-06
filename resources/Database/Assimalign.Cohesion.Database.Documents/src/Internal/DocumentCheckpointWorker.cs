using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Documents.Internal;

/// <summary>
/// The engine-owned checkpointer: checkpoints a database when its journal reaches
/// <see cref="DocumentDatabaseEngineOptions.CheckpointJournalSize"/>, or when
/// <see cref="DocumentDatabaseEngineOptions.CheckpointInterval"/> has passed since its last
/// checkpoint and its journal received records since (#1254). A checkpoint flushes all page
/// state by the database's durability policy and truncates the journal with continued LSNs.
/// </summary>
/// <remarks>
/// The pass, its lanes and its failure handling are the engines' shared checkpointer
/// (<see cref="DatabaseCheckpointWorker{TDatabase}"/>): each database's checkpoint runs on a lane of
/// its own, so a checkpoint that hangs or crawls in its device holds back its own database only,
/// and a failed checkpoint is that database's failure (#1268). A document database checkpoints its
/// data storage through its transaction coordinator, which defers the checkpoint to a statement
/// holding the apply gate. Before #1268 any failure escaped the pass and ended the worker for good.
/// A database its holder closed is never due (<see cref="IsCheckpointDue"/>), so a failure recorded
/// for it ends with the first pass after its backoff.
/// </remarks>
internal sealed class DocumentCheckpointWorker : DatabaseCheckpointWorker<DocumentDatabase>
{
    private readonly DocumentDatabaseEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentCheckpointWorker"/> class.
    /// </summary>
    /// <param name="engine">The document database engine whose open databases are checkpointed.</param>
    public DocumentCheckpointWorker(DocumentDatabaseEngine engine)
        : base(engine.Name, engine.EngineOptions.CheckpointInterval)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    protected override ManualResetEventSlim CheckpointNeededSignal => _engine.CheckpointNeededSignal;

    /// <inheritdoc />
    protected override DocumentDatabase[] GetDatabases() => _engine.GetInstanceSnapshot();

    /// <inheritdoc />
    protected override string GetName(DocumentDatabase database) => database.Name;

    /// <inheritdoc />
    protected override bool IsOffline(DocumentDatabase database) => database.IsOffline;

    /// <inheritdoc />
    protected override bool IsOpen(DocumentDatabase database) => _engine.IsOpen(database);

    /// <inheritdoc />
    /// <remarks>
    /// False for a database its holder closed (directly or through a session's
    /// <see cref="DocumentDatabaseSession.Database"/>), which the engine keeps registered until its close ends
    /// and then forgets. A close that was not idle (a writer the close kept in flight, #1226) leaves the
    /// journal untruncated, so the closed storage would stay due for a checkpoint it refuses, and a
    /// failure recorded for the database would never end.
    /// </remarks>
    protected override bool IsCheckpointDue(DocumentDatabase database, TimeSpan interval)
        => !database.IsClosed && database.DataStorage.IsCheckpointDue(interval);

    /// <inheritdoc />
    /// <remarks>
    /// A statement holding the apply gate takes the checkpoint over and runs it as it ends, so the
    /// checkpoint never waits on the database's own statements either.
    /// </remarks>
    protected override bool Checkpoint(DocumentDatabase database, TimeSpan interval, CancellationToken cancellationToken)
        => database.Coordinator.TryCheckpoint(TimeSpan.Zero, cancellationToken);
}
