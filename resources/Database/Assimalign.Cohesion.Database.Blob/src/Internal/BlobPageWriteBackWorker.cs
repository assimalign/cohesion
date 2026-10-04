using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Blob.Internal;

using Assimalign.Cohesion.Database.Blob.Storage;

/// <summary>
/// The engine-owned dirty-page writer: paced write-back of buffered pages between
/// checkpoints so a checkpoint's flush does not spike. Each pass writes a bounded
/// batch per open storage file set; the buffer pool's write-ahead gate guarantees
/// the journal is durable past a page's LSN before the page reaches the data file.
/// </summary>
/// <remarks>
/// A page write that fails leaves the page dirty in the pool (it is recorded clean only after
/// its write), so the failure is reported, the pass goes on to the next file set, and a later
/// pass writes the page (#1268). PostgreSQL's background writer treats a failed write the same
/// way: the buffer stays dirty and the writer retries after its error sleep
/// (<c>src/backend/postmaster/bgwriter.c:154-205</c>). An offline storage writes nothing.
/// </remarks>
internal sealed class BlobPageWriteBackWorker : DatabaseEngineWorker
{
    private readonly BlobDatabaseEngine _engine;

    internal BlobPageWriteBackWorker(BlobDatabaseEngine engine)
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
    protected override bool RunIterationCore(CancellationToken cancellationToken)
    {
        int batchSize = _engine.EngineOptions.PageWriteBackBatchSize;

        foreach (BlobStorage storage in _engine.GetStorageSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
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
                    ReportFailure(exception);
                }
            }
        }

        return true;
    }
}

