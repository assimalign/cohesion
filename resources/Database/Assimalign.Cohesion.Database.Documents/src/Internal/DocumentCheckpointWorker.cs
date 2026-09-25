using System;
using System.Threading;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed class DocumentCheckpointWorker : DatabaseEngineWorker
{
    private readonly DocumentDatabaseEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentCheckpointWorker"/> class.
    /// </summary>
    /// <param name="engine">The document database engine whose open databases are checkpointed.</param>
    public DocumentCheckpointWorker(DocumentDatabaseEngine engine)
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
