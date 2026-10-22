using System;
using System.Collections.Generic;
using System.Threading;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Internal;

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
internal sealed class DocumentPageWriteBackWorker : DatabaseWriteWorker<DocumentDatabase>
{
    private readonly DocumentDatabaseEngine _engine;

    internal DocumentPageWriteBackWorker(DocumentDatabaseEngine engine)
        : base(engine.Name + "/page-writeback", DatabaseEngineWorkerKind.PageWriteBack, engine.EngineOptions.PageWriteBackInterval)
    {
        _engine = engine;
    }

    protected override DocumentDatabase[] GetDatabases()
    {
        return _engine.GetInstanceSnapshot();
    }
    protected override string GetName(DocumentDatabase database) => database.Name;
    protected override bool IsClosed(DocumentDatabase database) => database.IsClosed;
    protected override bool IsOffline(DocumentDatabase database) => database.IsOffline;
    protected override bool IsOpen(DocumentDatabase database) => _engine.IsOpen(database);

    protected override DatabaseWriteResult RunDatabase(DocumentDatabase database, CancellationToken cancellationToken)
    {
        DatabaseWriteResult failures = default;
        int batch = _engine.EngineOptions.PageWriteBackBatchSize;
        TryWrite(() => database.DataStorage.WriteBackDirtyPages(batch), ref failures);
        return failures;
    }
}
