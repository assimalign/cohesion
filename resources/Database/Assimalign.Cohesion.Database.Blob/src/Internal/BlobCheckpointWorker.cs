using System;
using System.Threading;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob.Internal;

internal sealed class BlobCheckpointWorker : DatabaseEngineWorker
{
    private readonly BlobDatabaseEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobCheckpointWorker"/> class.
    /// </summary>
    /// <param name="engine">The blob engine whose database instances are checkpointed.</param>
    public BlobCheckpointWorker(BlobDatabaseEngine engine)
    {
        _engine = engine;
    }

    public override string Name => _engine.Name + "/checkpoint";
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.Checkpoint;
    public override TimeSpan Interval => _engine.EngineOptions.CheckpointInterval;
    public override void RunIteration(CancellationToken cancellationToken)
    {
        foreach (var database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try { database.Coordinator.Checkpoint(); }
            catch (StorageTransactionException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
