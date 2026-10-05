using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Blob.Internal;

/// <summary>
/// The engine-owned checkpointer: checkpoints a database when its journal reaches
/// <see cref="BlobDatabaseEngineOptions.CheckpointJournalSize"/>, or when
/// <see cref="BlobDatabaseEngineOptions.CheckpointInterval"/> has passed since its last
/// checkpoint and its journal received records since (#1254). A checkpoint flushes all page
/// state by the database's durability policy and truncates the journal with continued LSNs.
/// </summary>
/// <remarks>
/// The pass, its lanes and its failure handling are the engines' shared checkpointer
/// (<see cref="DatabaseCheckpointWorker{TDatabase}"/>): each database's checkpoint runs on a lane of
/// its own, so a checkpoint that hangs or crawls in its device holds back its own database only,
/// and a failed checkpoint is that database's failure (#1268). A blob database checkpoints its
/// data storage through its transaction coordinator, which defers the checkpoint to a statement
/// holding the apply gate. Before #1268 any failure escaped the pass and ended the worker for good.
/// </remarks>
internal sealed class BlobCheckpointWorker : DatabaseCheckpointWorker<BlobDatabaseInstance>
{
    private readonly BlobDatabaseEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobCheckpointWorker"/> class.
    /// </summary>
    /// <param name="engine">The blob database engine whose open databases are checkpointed.</param>
    public BlobCheckpointWorker(BlobDatabaseEngine engine)
        : base(engine.Name, engine.EngineOptions.CheckpointInterval)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    protected override ManualResetEventSlim CheckpointNeededSignal => _engine.CheckpointNeededSignal;

    /// <inheritdoc />
    protected override BlobDatabaseInstance[] GetDatabases() => _engine.GetInstanceSnapshot();

    /// <inheritdoc />
    protected override string GetName(BlobDatabaseInstance database) => database.Name;

    /// <inheritdoc />
    protected override bool IsOffline(BlobDatabaseInstance database) => database.IsOffline;

    /// <inheritdoc />
    protected override bool IsOpen(BlobDatabaseInstance database) => _engine.IsOpen(database);

    /// <inheritdoc />
    protected override bool IsCheckpointDue(BlobDatabaseInstance database, TimeSpan interval)
        => database.DataStorage.IsCheckpointDue(interval);

    /// <inheritdoc />
    /// <remarks>
    /// A statement holding the apply gate takes the checkpoint over and runs it as it ends, so the
    /// checkpoint never waits on the database's own statements either.
    /// </remarks>
    protected override bool Checkpoint(BlobDatabaseInstance database, TimeSpan interval, CancellationToken cancellationToken)
        => database.Coordinator.TryCheckpoint(TimeSpan.Zero, cancellationToken);
}
