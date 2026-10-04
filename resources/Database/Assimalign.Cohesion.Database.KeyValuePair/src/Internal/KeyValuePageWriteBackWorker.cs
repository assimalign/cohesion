using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;

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
/// (<c>src/backend/postmaster/bgwriter.c:154-205</c>). An offline database is skipped.
/// </remarks>
internal sealed class KeyValuePageWriteBackWorker : DatabaseEngineWorker
{
    private readonly KeyValueDatabaseEngine _engine;

    internal KeyValuePageWriteBackWorker(KeyValueDatabaseEngine engine)
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

        foreach (KeyValueDatabaseInstance database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Nothing of an offline database is written (#1243): neither file set, whichever
            // went offline. Each storage also refuses on its own.
            if (database.IsOffline)
            {
                continue;
            }

            WriteBack(database, database.DataStorage, batchSize);
            WriteBack(database, database.CatalogStorage, batchSize);
        }

        return true;
    }

    private void WriteBack(KeyValueDatabaseInstance database, KeyValueStorage storage, int batchSize)
    {
        try
        {
            storage.WriteBackDirtyPages(batchSize);
        }
        catch (ObjectDisposedException) when (!_engine.IsOpen(database))
        {
            // The snapshot can race a database drop; nothing to write back.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The pages stay dirty and a later pass writes them; an offline database's refusal
            // is not the worker's failure (the engine lists the database offline).
            if (!database.IsOffline)
            {
                ReportFailure(exception);
            }
        }
    }
}
