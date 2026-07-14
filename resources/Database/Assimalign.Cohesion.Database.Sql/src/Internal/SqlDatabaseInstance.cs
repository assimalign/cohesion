using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// Internal implementation of a SQL database instance: the data storage, the
/// dedicated catalog storage, the catalog opened over it, and the transaction
/// coordinator — the per-database MVCC composition (transaction manager, lock
/// manager, version store) every session binds to.
/// </summary>
internal sealed class SqlDatabaseInstance : ISqlDatabase
{
    private readonly SqlStorage _storage;
    private readonly SqlStorage _catalogStorage;
    private readonly ISqlCatalog _catalog;
    private readonly SqlTransactionCoordinator _coordinator;
    private bool _disposed;

    internal SqlDatabaseInstance(string name, IDatabaseEngine engine, SqlStorage storage, SqlStorage catalogStorage, bool recover = false)
    {
        Name = name;
        Engine = engine;
        _storage = storage;
        _catalogStorage = catalogStorage;
        _catalog = SqlCatalog.Open(catalogStorage);
        _coordinator = new SqlTransactionCoordinator(storage);

        if (recover)
        {
            // Reopened storage: classify the recovered journal, purge unproven
            // writers from the version store, then checkpoint (the open-time
            // checkpoint the storage strategy deferred).
            _coordinator.Recover();
        }

        UpgradeRecordSpaceIfNeeded();
    }

    /// <summary>
    /// Upgrades an older record space in place, at open, before any session
    /// exists: a pre-MVCC (version-1) space first gains version stamps, and a
    /// pre-chain (version-2) space is relocated into per-object page chains; the
    /// catalog then persists the current format version. Each stage rides one
    /// data-storage transaction (all-or-nothing) and is idempotent across the
    /// two-storage crash window, because the marker write is last: a crash after
    /// a stage's commit re-runs a provably-detectable no-op (see each stage).
    /// </summary>
    private void UpgradeRecordSpaceIfNeeded()
    {
        int version = _catalog.RecordSpaceFormatVersion;

        if (version >= SqlRowCodec.RecordSpaceFormatVersion)
        {
            return;
        }

        if (version < 2)
        {
            UpgradeUnstampedRecords();
        }

        RelocateRecordsToOwnerChains();

        // Marker last. Synchronous over the ValueTask by design: catalog writes
        // complete synchronously and instance open is a synchronous path.
        _catalog.SetRecordSpaceFormatVersionAsync(SqlRowCodec.RecordSpaceFormatVersion)
            .AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// The version-1 → version-2 stage: every data record gains a zeroed 16-byte
    /// version-stamp header (writer zero reads as committed bootstrap data,
    /// visible to every snapshot). Idempotent: a version-1 record always begins
    /// with the tuple codec's nonzero <c>Int64</c> tag byte, so a record already
    /// carrying a zeroed stamp header is provably upgraded and skipped when a
    /// crash replays the stage on the next open.
    /// </summary>
    private void UpgradeUnstampedRecords()
    {
        var records = new List<(PageId PageId, int SlotIndex, byte[] Data)>();

        using (var iterator = _storage.GetUnitIterator())
        {
            while (iterator.MoveNext())
            {
                var unit = iterator.Current;

                if (!IsUpgraded(unit.Data.Span))
                {
                    records.Add((unit.PageId, unit.SlotIndex, unit.Data.ToArray()));
                }
            }
        }

        if (records.Count > 0)
        {
            using var transaction = _storage.BeginTransaction();

            foreach (var (pageId, slotIndex, data) in records)
            {
                byte[] upgraded = SqlRowCodec.UpgradeUnstamped(data);

                try
                {
                    _storage.UpdateRow(transaction, pageId, slotIndex, upgraded);
                }
                catch (SlottedPageException)
                {
                    // The stamp header outgrew the slot: relocate.
                    _storage.DeleteRow(transaction, pageId, slotIndex);
                    _storage.InsertRow(transaction, upgraded);
                }
            }

            transaction.Commit();
        }

        static bool IsUpgraded(ReadOnlySpan<byte> record)
        {
            // A version-1 record starts with the tuple codec's Int64 tag byte
            // (never zero); an upgraded-but-unmarked record starts with the
            // zeroed bootstrap stamp header.
            if (record.Length < SqlRowCodec.StampHeaderSize)
            {
                return false;
            }

            return !record.Slice(0, SqlRowCodec.StampHeaderSize).ContainsAnyExcept((byte)0);
        }
    }

    /// <summary>
    /// The version-2 → version-3 stage: rows move out of the shared (owner-zero)
    /// page stream into their table's per-object page chain, stamps preserved
    /// verbatim (visibility is unchanged by the move), and the emptied shared
    /// pages are released. Rows whose object id no longer exists in the catalog —
    /// residue of tables dropped before chains existed — are dropped rather than
    /// moved (the catalog is the schema authority; such rows are unreachable).
    /// Idempotent: the stage reads only owner-zero pages, and a moved record
    /// lives on an owner-tagged page, so a crash between the relocation commit
    /// and the marker write re-runs an empty pass.
    /// </summary>
    private void RelocateRecordsToOwnerChains()
    {
        var knownObjects = new HashSet<ulong>();
        foreach (var table in _catalog.Tables)
        {
            knownObjects.Add(table.ObjectId);
        }

        var moves = new List<(PageId PageId, int SlotIndex, ulong ObjectId, byte[] Data)>();

        using (var iterator = _storage.GetUnitIterator(0))
        {
            while (iterator.MoveNext())
            {
                var unit = iterator.Current;

                if (unit.Data.Length <= SqlRowCodec.StampHeaderSize)
                {
                    continue;
                }

                var reader = new DatabaseKeyReader(unit.Data.Span[SqlRowCodec.StampHeaderSize..]);
                ulong objectId = (ulong)reader.ReadInt64();

                moves.Add((unit.PageId, unit.SlotIndex, objectId, unit.Data.ToArray()));
            }
        }

        if (moves.Count == 0 && _storage.GetOwnerPages(0).Count == 0)
        {
            return;
        }

        using var relocation = _storage.BeginTransaction();

        foreach (var (pageId, slotIndex, objectId, data) in moves)
        {
            _storage.DeleteRow(relocation, pageId, slotIndex);

            if (knownObjects.Contains(objectId))
            {
                _storage.InsertRow(relocation, objectId, data);
            }
        }

        // The shared pages are empty now — release the whole owner-zero chain so
        // the space returns to the allocator.
        _storage.FreeOwnerPages(relocation, 0);
        relocation.Commit();
    }

    /// <inheritdoc />
    public DatabaseName Name { get; }

    /// <inheritdoc />
    public IDatabaseEngine Engine { get; }

    /// <summary>
    /// Gets the data storage file set, for the engine's background workers.
    /// </summary>
    internal SqlStorage DataStorage => _storage;

    /// <summary>
    /// Gets the dedicated catalog storage file set, for the engine's background workers.
    /// </summary>
    internal SqlStorage CatalogStorage => _catalogStorage;

    /// <summary>
    /// Gets the database's catalog (schema authority), for the engine's background
    /// workers and tests.
    /// </summary>
    internal ISqlCatalog Catalog => _catalog;

    /// <summary>
    /// Gets the database's transaction coordinator (the MVCC composition sessions
    /// bind to), for the engine's background workers and tests.
    /// </summary>
    internal SqlTransactionCoordinator Coordinator => _coordinator;

    /// <summary>
    /// Checkpoints the data storage through the coordinator, so the truncating
    /// checkpoint record carries the sequences of in-flight logical transactions
    /// (recovery classification stays sound). The catalog storage has no logical
    /// transactions above it and checkpoints directly.
    /// </summary>
    internal void CheckpointDataStorage() => _coordinator.Checkpoint();

    /// <inheritdoc />
    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var executor = new SqlQueryExecutor(_storage, _catalog);
        var session = new SqlDatabaseSession(this, _coordinator, executor);

        return new ValueTask<IDatabaseSession>(session);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The coordinator first: the manager aborts every still-active logical
        // transaction (rolling its paired bracket back) while the storage is
        // still open. Synchronous over the ValueTask by design — the in-process
        // implementations complete synchronously.
        _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _storage.Dispose();
        _catalogStorage.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _coordinator.DisposeAsync().ConfigureAwait(false);
        await _storage.DisposeAsync().ConfigureAwait(false);
        await _catalogStorage.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
