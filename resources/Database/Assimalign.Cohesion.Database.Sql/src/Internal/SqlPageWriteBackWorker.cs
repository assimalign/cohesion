using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;

/// <summary>
/// The engine-owned dirty-page writer: paced write-back of buffered pages between
/// checkpoints so a checkpoint's flush does not spike. Each pass writes a bounded
/// batch per open storage file set; the buffer pool's write-ahead gate guarantees
/// the journal is durable past a page's LSN before the page reaches the data file.
/// </summary>
internal sealed class SqlPageWriteBackWorker : DatabaseEngineWorker
{
    private readonly SqlDatabaseEngine _engine;

    internal SqlPageWriteBackWorker(SqlDatabaseEngine engine)
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
    public override void RunIteration(CancellationToken cancellationToken)
    {
        int batchSize = _engine.EngineOptions.PageWriteBackBatchSize;

        foreach (SqlDatabaseInstance database in _engine.GetInstanceSnapshot())
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

            WriteBack(database.DataStorage, batchSize);
            WriteBack(database.CatalogStorage, batchSize);
        }
    }

    private static void WriteBack(SqlStorage storage, int batchSize)
    {
        try
        {
            storage.WriteBackDirtyPages(batchSize);
        }
        catch (ObjectDisposedException)
        {
            // The snapshot can race a database drop; nothing to write back.
        }
    }
}
