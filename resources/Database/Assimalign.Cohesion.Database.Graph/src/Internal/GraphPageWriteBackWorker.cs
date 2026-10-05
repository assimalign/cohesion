using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Internal;

using Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>
/// The engine-owned dirty-page writer: paced write-back of buffered pages between
/// checkpoints so a checkpoint's flush does not spike. Each pass writes a bounded
/// batch per open storage file set; the buffer pool's write-ahead gate guarantees
/// the journal is durable past a page's LSN before the page reaches the data file.
/// </summary>
/// <remarks>
/// A page write that fails leaves the page dirty in the pool (it is recorded clean only after
/// its write), so the failure is reported for its database, the pass goes on to the next one, and
/// a pass after <see cref="DatabaseEngineWorker.FailureBackoff"/> writes the page (#1268); the
/// other databases keep the worker's full pace meanwhile. PostgreSQL leaves a buffer whose write
/// failed dirty for a later write (<c>AbortBufferIO</c>,
/// <c>src/backend/storage/buffer/bufmgr.c:7469-7502</c>), and its background writer sleeps a
/// second after the error before it writes again (<c>src/backend/postmaster/bgwriter.c:154-205</c>).
/// An offline storage writes nothing.
/// </remarks>
internal sealed class GraphPageWriteBackWorker : DatabaseEngineWorker
{
    private readonly GraphDatabaseEngine _engine;

    internal GraphPageWriteBackWorker(GraphDatabaseEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public override string Name => _engine.Name + "/page-writeback";

    /// <inheritdoc />
    public override DatabaseEngineWorkerKind Kind => DatabaseEngineWorkerKind.PageWriteBack;

    /// <inheritdoc />
    public override TimeSpan Interval => _engine.EngineOptions.PageWriteBackInterval;

    /// <inheritdoc />
    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        int batchSize = _engine.EngineOptions.PageWriteBackBatchSize;

        foreach (GraphStorage storage in _engine.GetStorageSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // A storage names its database. An offline one is not begun: nothing of it is written
            // (#1243), the engine reports it, and a failure the worker recorded for it ends.
            if (storage.IsOffline || !BeginDatabase(storage.Name))
            {
                continue;
            }

            try
            {
                storage.WriteBackDirtyPages(batchSize);
            }
            catch (ObjectDisposedException) when (!_engine.IsOpen(storage))
            {
                // The snapshot can race a database drop; nothing to write back.
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The pages stay dirty and a later pass writes them; an offline storage's refusal
                // is not the worker's failure (the engine lists its database offline).
                if (!storage.IsOffline)
                {
                    ReportFailure(storage.Name, exception);
                }
            }
        }
    }
}

