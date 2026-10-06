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
/// its write), so the failure is reported for its database, the pass goes on to the next one, and
/// a pass after <see cref="DatabaseEngineWorker.FailureBackoff"/> writes the page (#1268); the
/// other databases keep the worker's full pace meanwhile. PostgreSQL leaves a buffer whose write
/// failed dirty for a later write (<c>AbortBufferIO</c>,
/// <c>src/backend/storage/buffer/bufmgr.c:7469-7502</c>), and its background writer sleeps a
/// second after the error before it writes again (<c>src/backend/postmaster/bgwriter.c:154-205</c>).
/// An offline database is skipped, and so is a database its holder closed, whose close flushes
/// both file sets.
/// </remarks>
internal sealed class KeyValuePageWriteBackWorker : DatabaseEngineWorker
{
    private readonly KeyValueDatabaseEngine _engine;

    internal KeyValuePageWriteBackWorker(KeyValueDatabaseEngine engine)
        : base(engine.Name + "/page-writeback", DatabaseEngineWorkerKind.PageWriteBack, engine.EngineOptions.PageWriteBackInterval)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        int batchSize = _engine.EngineOptions.PageWriteBackBatchSize;

        foreach (KeyValueDatabase database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Nothing of an offline database is written (#1243): neither file set, whichever
            // went offline. Each storage also refuses on its own. Nor is anything of a database
            // its holder closed: its close flushes its file sets, and the engine keeps the closed
            // instance registered (its reopen returns it).
            if (database.IsClosed || database.IsOffline || !BeginDatabase(database.Name))
            {
                continue;
            }

            WriteBack(database, database.DataStorage, batchSize);
            WriteBack(database, database.CatalogStorage, batchSize);
        }
    }

    private void WriteBack(KeyValueDatabase database, KeyValueStorage storage, int batchSize)
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
                ReportFailure(database.Name, exception);
            }
        }
    }
}
