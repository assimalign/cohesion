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
    public override void RunIteration(CancellationToken cancellationToken)
    {
        int batchSize = _engine.EngineOptions.PageWriteBackBatchSize;

        foreach (KeyValueStorage storage in _engine.GetStorageSnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

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
}
