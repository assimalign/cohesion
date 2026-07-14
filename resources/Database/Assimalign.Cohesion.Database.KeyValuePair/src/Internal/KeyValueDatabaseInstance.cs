using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.KeyValuePair.Catalog;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Internal implementation of a key-value database instance: the data storage,
/// the dedicated catalog storage, the catalog opened over it, the transaction
/// coordinator — the per-database MVCC composition (transaction manager, lock
/// manager, version store) every session binds to — and the <b>primary key
/// index</b>: the unique B+Tree over the key space that is the model's primary
/// structure (key → packed entry location; the index-primary composition).
/// </summary>
internal sealed class KeyValueDatabaseInstance : IKeyValueDatabase
{
    private readonly KeyValueStorage _storage;
    private readonly KeyValueStorage _catalogStorage;
    private readonly IKeyValueCatalog _catalog;
    private readonly KeyValueTransactionCoordinator _coordinator;
    private readonly IIndexManager _indexManager;
    private readonly IIndex _primaryIndex;
    private bool _disposed;

    internal KeyValueDatabaseInstance(string name, IDatabaseEngine engine, KeyValueStorage storage, KeyValueStorage catalogStorage, bool recover = false)
    {
        Name = name;
        Engine = engine;
        _storage = storage;
        _catalogStorage = catalogStorage;
        _catalog = KeyValueCatalog.Open(catalogStorage);
        _coordinator = new KeyValueTransactionCoordinator(storage);

        if (_catalog.EntrySpaceFormatVersion > KeyValueRecordCodec.EntrySpaceFormatVersion)
        {
            throw new DatabaseException(
                $"Database '{name}' uses entry-space format version {_catalog.EntrySpaceFormatVersion}, " +
                $"newer than this engine understands ({KeyValueRecordCodec.EntrySpaceFormatVersion}).");
        }

        // Re-attach the persisted primary index before recovery: the open-time
        // scrub must be able to purge unproven writers' entries out of the tree.
        // Index pages live in the SAME data file set as entry records (the
        // transactional page surface), so storage recovery has already replayed
        // them by the time the manager attaches.
        _indexManager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = storage,
            TransactionSource = _coordinator,
            LockManager = _coordinator.LockManager,
            ExistingIndexes = _catalog.GetIndexRegistrations(),
        });

        if (recover)
        {
            // Reopened storage: classify the recovered journal, purge unproven
            // writers from the record space AND the primary index, then
            // checkpoint (the open-time checkpoint the storage strategy
            // deferred — it must come last, because truncation destroys the
            // lifecycle records classification reads).
            var plan = _coordinator.AnalyzeAndScrub();

            if (plan.Aborted.Count > 0)
            {
                using var scrub = _storage.BeginTransaction();
                _indexManager.PurgeWritersAsync(scrub, plan.Aborted)
                    .AsTask().GetAwaiter().GetResult();
                scrub.Commit();
            }

            _coordinator.CompleteRecovery();
        }

        _primaryIndex = EnsurePrimaryIndex();
    }

    /// <summary>
    /// Resolves the primary key index, bootstrapping it when it does not exist —
    /// at database creation, or on a reopen whose creation crashed between the
    /// tree build and the registration persist (the crash window leaves only an
    /// orphaned tree root — a safe leak, the SQL index-DDL posture). The
    /// bootstrap bracket commits durably (the self-committing DDL posture: the
    /// catalog registration commits independently and must never describe a tree
    /// a crash could revert), and the registration + format marker persist as
    /// catalog self-commits after it.
    /// </summary>
    private IIndex EnsurePrimaryIndex()
    {
        if (_indexManager.TryGetIndex(KeyValueOperationExecutor.KeySpaceObjectId, KeyValueOperationExecutor.PrimaryIndexName, out var existing))
        {
            return existing;
        }

        // Synchronous over the ValueTask by design: the in-process
        // implementations complete synchronously and instance open is a
        // synchronous path (the SqlDatabaseInstance precedent).
        var context = _coordinator.BeginAsync(IsolationLevel.Snapshot)
            .AsTask().GetAwaiter().GetResult();

        IIndex index;
        try
        {
            index = _coordinator.ApplyStatementAsync(
                context,
                bracket => _indexManager.CreateIndexAsync(
                    context,
                    KeyValueOperationExecutor.KeySpaceObjectId,
                    new IndexDefinition(KeyValueOperationExecutor.PrimaryIndexName, IndexKind.BTree, IsUnique: true)),
                durable: true).AsTask().GetAwaiter().GetResult();

            _coordinator.CommitAsync(context).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            if (context.State == TransactionState.Active)
            {
                _coordinator.RollbackAsync(context).AsTask().GetAwaiter().GetResult();
            }

            throw;
        }

        _catalog.SaveIndexRegistrationsAsync(((IIndexRegistry)_indexManager).ExportRegistrations())
            .AsTask().GetAwaiter().GetResult();
        _catalog.SetEntrySpaceFormatVersionAsync(KeyValueRecordCodec.EntrySpaceFormatVersion)
            .AsTask().GetAwaiter().GetResult();

        return index;
    }

    /// <inheritdoc />
    public DatabaseName Name { get; }

    /// <inheritdoc />
    public IDatabaseEngine Engine { get; }

    /// <summary>
    /// Gets the data storage file set, for the engine's background workers.
    /// </summary>
    internal KeyValueStorage DataStorage => _storage;

    /// <summary>
    /// Gets the dedicated catalog storage file set, for the engine's background workers.
    /// </summary>
    internal KeyValueStorage CatalogStorage => _catalogStorage;

    /// <summary>
    /// Gets the database's catalog, for the engine's background workers and tests.
    /// </summary>
    internal IKeyValueCatalog Catalog => _catalog;

    /// <summary>
    /// Gets the database's index manager (the live B+Tree directory over the data
    /// file set), for the engine's background workers and tests.
    /// </summary>
    internal IIndexManager IndexManager => _indexManager;

    /// <summary>
    /// Gets the database's transaction coordinator (the MVCC composition sessions
    /// bind to), for the engine's background workers and tests.
    /// </summary>
    internal KeyValueTransactionCoordinator Coordinator => _coordinator;

    /// <summary>
    /// Persists the index manager's current registrations when they drifted from
    /// the stored set — root page ids change on splits, so this runs at the
    /// engine's persistence points (checkpoint passes and disposal) in addition
    /// to the creation bootstrap itself.
    /// </summary>
    internal void SaveIndexRegistrationsIfChanged()
    {
        var current = ((IIndexRegistry)_indexManager).ExportRegistrations();
        var stored = _catalog.GetIndexRegistrations();

        if (RegistrationsEqual(current, stored))
        {
            return;
        }

        // Synchronous over the ValueTask by design: catalog writes complete
        // synchronously (worker passes and disposal are synchronous paths).
        _catalog.SaveIndexRegistrationsAsync(current).AsTask().GetAwaiter().GetResult();

        static bool RegistrationsEqual(
            IReadOnlyList<BTreeIndexRegistration> left,
            IReadOnlyList<BTreeIndexRegistration> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            // Registration sets are tiny; order-insensitive comparison by value
            // (BTreeIndexRegistration is a record).
            foreach (var registration in left)
            {
                bool found = false;

                foreach (var candidate in right)
                {
                    if (registration == candidate)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }
    }

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

        var executor = new KeyValueOperationExecutor(_storage, _primaryIndex);
        var session = new KeyValueDatabaseSession(this, _coordinator, executor);

        return new ValueTask<IDatabaseSession>(session);
    }

    // ── Typed model surface (conveniences over the typed-request seam) ──

    /// <inheritdoc />
    public async ValueTask<KeyValueEntry?> GetAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        var result = await RequireOwnSession(session).ExecuteAsync(new KeyValueGetRequest(key), cancellationToken).ConfigureAwait(false);
        var set = (QueryResultSet)result;

        await using (set.ConfigureAwait(false))
        {
            await foreach (QueryRow row in set.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                return DecodeEntry(row);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<KeyValuePutResult> PutAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, KeyValuePutOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await RequireOwnSession(session).ExecuteAsync(new KeyValuePutRequest(key, value, options), cancellationToken).ConfigureAwait(false);
        var set = (QueryResultSet)result;

        await using (set.ConfigureAwait(false))
        {
            await foreach (QueryRow row in set.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                return new KeyValuePutResult(row.GetBoolean(0), row.IsNull(1) ? null : row.GetInt64(1));
            }
        }

        throw new DatabaseException("The put command returned no outcome row.");
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryDeleteAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, long? expectedETag = null, CancellationToken cancellationToken = default)
    {
        var result = await RequireOwnSession(session).ExecuteAsync(new KeyValueDeleteRequest(key, expectedETag), cancellationToken).ConfigureAwait(false);
        return result.AffectedCount > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> ExistsAsync(IDatabaseSession session, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        var result = await RequireOwnSession(session).ExecuteAsync(new KeyValueExistsRequest(key), cancellationToken).ConfigureAwait(false);
        var set = (QueryResultSet)result;

        await using (set.ConfigureAwait(false))
        {
            await foreach (QueryRow row in set.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                return row.GetBoolean(0);
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KeyValueEntry> ScanAsync(IDatabaseSession session, KeyValueScanOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await RequireOwnSession(session).ExecuteAsync(new KeyValueScanRequest(options), cancellationToken).ConfigureAwait(false);
        var set = (QueryResultSet)result;

        await using (set.ConfigureAwait(false))
        {
            await foreach (QueryRow row in set.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return DecodeEntry(row);
            }
        }
    }

    private static KeyValueEntry DecodeEntry(QueryRow row)
        => new(row.GetBytes(0), row.GetBytes(1), row.GetInt64(2));

    private KeyValueDatabaseSession RequireOwnSession(IDatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session is not KeyValueDatabaseSession owned || !ReferenceEquals(owned.Database, this))
        {
            throw new DatabaseException("The session does not belong to this key-value database.");
        }

        return owned;
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
        // transaction (undoing its stamps through the version store's ledger)
        // while the storage is still open. Synchronous over the ValueTask by
        // design — the in-process implementations complete synchronously.
        // Registrations re-export after the aborts and before the storages close.
        _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        SaveIndexRegistrationsIfChanged();
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
        SaveIndexRegistrationsIfChanged();
        await _storage.DisposeAsync().ConfigureAwait(false);
        await _catalogStorage.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
