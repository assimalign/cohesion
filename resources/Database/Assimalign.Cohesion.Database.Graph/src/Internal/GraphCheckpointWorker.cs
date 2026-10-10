using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// The engine-owned checkpointer: checkpoints a database when its journal reaches
/// <see cref="GraphDatabaseEngineOptions.CheckpointJournalSize"/>, or when
/// <see cref="GraphDatabaseEngineOptions.CheckpointInterval"/> has passed since its last
/// checkpoint and its journal received records since (#1254). A checkpoint flushes all page
/// state by the database's durability policy and truncates the journal with continued LSNs.
/// </summary>
/// <remarks>
/// The pass, its lanes and its failure handling are the engines' shared checkpointer
/// (<see cref="DatabaseCheckpointWorker{TDatabase}"/>): each database's checkpoint runs on a lane of
/// its own, so a checkpoint that hangs or crawls in its device holds back its own database only,
/// and a failed checkpoint is that database's failure (#1268). A graph database checkpoints its
/// data storage through its transaction coordinator, which defers the checkpoint to a statement
/// holding the apply gate. Before #1268 any failure escaped the pass and ended the worker for good.
/// A database its holder closed is never due (<see cref="IsCheckpointDue"/>), so a failure recorded
/// for it ends with the first pass after its backoff.
/// </remarks>
internal sealed class GraphCheckpointWorker : DatabaseCheckpointWorker<GraphDatabase>
{
    private readonly GraphDatabaseEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="GraphCheckpointWorker"/> class.
    /// </summary>
    /// <param name="engine">The graph database engine whose open databases are checkpointed.</param>
    public GraphCheckpointWorker(GraphDatabaseEngine engine)
        : base(engine.Name, engine.EngineOptions.CheckpointInterval, engine.JournalSizeLimit)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    protected override long GetJournalLength(GraphDatabase database) => database.DataStorage.JournalLength;

    /// <inheritdoc />
    protected override ManualResetEventSlim CheckpointNeededSignal => _engine.CheckpointNeededSignal;

    /// <inheritdoc />
    protected override GraphDatabase[] GetDatabases() => _engine.GetInstanceSnapshot();

    /// <inheritdoc />
    protected override string GetName(GraphDatabase database) => database.Name;

    /// <inheritdoc />
    protected override bool IsOffline(GraphDatabase database) => database.IsOffline;

    /// <inheritdoc />
    protected override bool IsOpen(GraphDatabase database) => _engine.IsOpen(database);

    /// <inheritdoc />
    /// <remarks>
    /// False for a database its holder closed (directly; a session's
    /// <see cref="GraphDatabaseSession.Database"/> is the same instance), which the engine keeps
    /// registered until its close ends and then forgets it. A close that was not idle (a writer the
    /// close kept in flight, #1226) leaves the journal untruncated, so the closed storage would stay
    /// due for a checkpoint it refuses, and a failure recorded for the database would never end.
    /// </remarks>
    protected override bool IsCheckpointDue(GraphDatabase database, TimeSpan interval)
        => !database.IsClosed && database.DataStorage.IsCheckpointDue(interval);

    /// <inheritdoc />
    /// <remarks>
    /// A statement holding the apply gate takes the checkpoint over and runs it as it ends, so the
    /// checkpoint never waits on the database's own statements either.
    /// </remarks>
    protected override bool Checkpoint(GraphDatabase database, TimeSpan interval, CancellationToken cancellationToken)
        => database.Coordinator.TryCheckpoint(TimeSpan.Zero, cancellationToken);
}
