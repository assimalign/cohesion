using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>
/// The engine-owned dirty-page writer: paced write-back of buffered pages between
/// checkpoints so a checkpoint's flush does not spike. Each pass writes a bounded
/// batch per open database; the buffer pool's write-ahead gate guarantees the journal
/// is durable past a page's LSN before the page reaches the data file.
/// </summary>
/// <remarks>
/// <para>
/// A page write that fails leaves the page dirty in the pool (it is recorded clean only after
/// its write), so the failure is reported for its database, the pass goes on to the next one, and
/// a pass after <see cref="DatabaseEngineWorker.FailureBackoff"/> writes the page (#1268); the
/// other databases keep the worker's full pace meanwhile. PostgreSQL leaves a buffer whose write
/// failed dirty for a later write (<c>AbortBufferIO</c>,
/// <c>src/backend/storage/buffer/bufmgr.c:7469-7502</c>), and its background writer sleeps a
/// second after the error before it writes again (<c>src/backend/postmaster/bgwriter.c:154-205</c>).
/// An offline database is skipped.
/// </para>
/// <para>
/// The pass visits the engine's open databases, not their storages, and reports under the
/// database's name in the engine: a storage's own name is read from its file header when it
/// opens, so a database opened from a copied file set carries the original's. Reported under it,
/// the copy's persistent failures would take the original offline (owner decision 25), and the
/// two would share one failure record.
/// </para>
/// </remarks>
internal sealed class GraphPageWriteBackWorker : DatabaseEngineWorker
{
    private readonly GraphDatabaseEngine _engine;

    internal GraphPageWriteBackWorker(GraphDatabaseEngine engine)
        : base(engine.Name + "/page-writeback", DatabaseEngineWorkerKind.PageWriteBack, engine.EngineOptions.PageWriteBackInterval)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    protected override void RunIterationCore(CancellationToken cancellationToken)
    {
        int batchSize = _engine.EngineOptions.PageWriteBackBatchSize;

        foreach (GraphDatabase database in _engine.GetInstanceSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Nothing of an offline database is written (#1243): the engine reports it, and a
            // failure the worker recorded for it ends. Nor of a database its holder closed: the
            // engine keeps it registered until its close ends, then forgets it.
            if (database.IsClosed || database.IsOffline || !BeginDatabase(database.Name))
            {
                continue;
            }

            try
            {
                database.DataStorage.WriteBackDirtyPages(batchSize);
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
}
