using System;
using System.Threading;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob.Internal;

internal sealed class BlobCheckpointWorker(BlobDatabaseEngine engine) : DatabaseEngineWorker
{
    public override string Name => engine.Name + "/checkpoint";
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.Checkpoint;
    public override TimeSpan Interval => engine.EngineOptions.CheckpointInterval;
    public override void RunIteration(CancellationToken cancellationToken)
    {
        foreach (var database in engine.GetInstanceSnapshot())
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
