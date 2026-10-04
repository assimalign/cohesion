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
using Assimalign.Cohesion.Database.Storage;
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
    private readonly TransactionCoordinator _coordinator;
    private readonly IIndexManager _indexManager;
    private readonly IIndex _primaryIndex;
    private bool _disposed;

    internal KeyValueDatabaseInstance(string name, IDatabaseEngine engine, KeyValueStorage storage, KeyValueStorage catalogStorage, bool recover = false)
    {
        Name = name;
        Engine = engine;
        _storage = storage;
        _catalogStorage = catalogStorage;

        // The two file sets go offline together, the moment either does (#1243): a worker that
        // writes back the other set's pages or flushes its journal would otherwise change a
        // file of the database after the failure, until something next read the offline state.
        _storage.OnOffline = _catalogStorage.TakeOffline;
        _catalogStorage.OnOffline = _storage.TakeOffline;
        if ((_storage.OfflineError ?? _catalogStorage.OfflineError) is { } alreadyOffline)
        {
            _storage.TakeOffline(alreadyOffline);
            _catalogStorage.TakeOffline(alreadyOffline);
        }

        _catalog = KeyValueCatalog.Open(catalogStorage);
        _coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, new KeyValueTransactionRecordSpace(storage));

        if (engine is KeyValueDatabaseEngine owner)
        {
            // A deferred undo is retried on its own backoff, from about 100 ms up to the
            // maintenance interval, and the purge worker wakes for it (#1226).
            _coordinator.DeferredUndoRetryLimit = owner.EngineOptions.MaintenanceInterval;
            _coordinator.DeferredUndoRetryDelay = owner.EngineOptions.DeferredUndoRetryDelay;
            _coordinator.OnUndoDeferred = owner.UndoDeferredSignal.Set;
        }

        // The format gate reads the catalog alone, before the primary index is
        // attached or recovery writes anything.
        try
        {
            ThrowIfFormatIsNotCurrent(name, _catalog);
        }
        catch
        {
            _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        // Re-attach the persisted primary index before recovery: the open-time
        // scrub must be able to purge unproven writers' entries out of the tree.
        // Index pages live in the SAME data file set as entry records (the
        // transactional page surface), so storage recovery has already replayed
        // them by the time the manager attaches. The manager checks the tree's
        // page format as it attaches it (Indexing owns that format, #1194): a
        // primary index written in another B-tree page format refuses the open
        // here, before recovery writes anything.
        try
        {
            _indexManager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
            {
                Storage = storage,
                TransactionSource = new StatementTransactionSource(_coordinator),
                LockManager = _coordinator.LockManager,
                ExistingIndexes = _catalog.GetIndexRegistrations(),
            });
        }
        catch (IndexFormatException exception)
        {
            _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new DatabaseException($"Database '{name}' cannot be opened. {exception.Message}", exception);
        }

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
    /// a crash could revert), and the format marker, then the registration, persist
    /// as catalog self-commits after it.
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

        // The marker first: a catalog that registers a primary index then always
        // carries the format of the engine that built it, which is what the format
        // gate relies on (a crash between the two leaves a marker and no
        // registration, and the next open bootstraps again).
        _catalog.SetEntrySpaceFormatVersionAsync(KeyValueRecordCodec.EntrySpaceFormatVersion)
            .AsTask().GetAwaiter().GetResult();
        _catalog.SaveIndexRegistrationsAsync(((IIndexRegistry)_indexManager).ExportRegistrations())
            .AsTask().GetAwaiter().GetResult();

        return index;
    }

    /// <summary>
    /// The format gate: refuses a database whose catalog marker is not this engine's
    /// entry-space format (<see cref="KeyValueRecordCodec.EntrySpaceFormatVersion"/>),
    /// before the primary index is attached or recovery writes anything. An engine
    /// before #1194 refuses this engine's databases the same way, because it rejects a
    /// marker newer than its own.
    /// </summary>
    /// <remarks>
    /// A catalog that registers no primary index describes a creation interrupted
    /// before the registration persisted. Nothing can have been written through such
    /// a database, since every write goes through the primary index, so the open goes
    /// on to bootstrap the index and stamp the current marker, whatever older or absent
    /// marker it read (an absent marker reads as 1). Every other database must carry
    /// exactly the current marker: this engine stamps it before it registers the index.
    /// </remarks>
    /// <exception cref="DatabaseException">The database is on another entry-space format.</exception>
    private static void ThrowIfFormatIsNotCurrent(string name, IKeyValueCatalog catalog)
    {
        int version = catalog.EntrySpaceFormatVersion;
        int current = KeyValueRecordCodec.EntrySpaceFormatVersion;

        if (version == current || (version < current && catalog.GetIndexRegistrations().Count == 0))
        {
            return;
        }

        string remedy = version < current
            ? "This engine does not upgrade databases written in an older format: export its data with the engine that " +
              "wrote it, drop the database (DropDatabaseAsync) and create it again with this engine, then reload the data " +
              "(on-disk format upgrades are tracked by assimalign/cohesion#1152)."
            : "The database was written by a newer engine; open it with that engine.";

        throw new DatabaseException(
            $"Database '{name}' uses entry-space format {version}, but this engine supports only format {current}. {remedy}");
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
    internal TransactionCoordinator Coordinator => _coordinator;

    /// <summary>
    /// Persists the index manager's current registrations when they differ from
    /// the stored set. A tree's root page stays fixed through splits (#1159), so
    /// this normally finds nothing to write; it runs at the engine's persistence
    /// points (checkpoint passes and disposal), in addition to the creation
    /// bootstrap itself, as a backstop.
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
    /// <param name="cancellationToken">Cancels the wait for the coordinator's apply gate.</param>
    internal void CheckpointDataStorage(CancellationToken cancellationToken = default) => _coordinator.Checkpoint(cancellationToken);

    /// <summary>
    /// Checkpoints the data storage through the coordinator without waiting for a statement:
    /// when one holds the apply gate, the checkpoint is deferred to its end
    /// (<see cref="TransactionCoordinator.TryCheckpoint"/>), so the checkpoint worker never
    /// waits on one database while the others' journals grow.
    /// </summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>True when the checkpoint ran; false when it was deferred to the statement applying.</returns>
    internal bool TryCheckpointDataStorage(CancellationToken cancellationToken) => _coordinator.TryCheckpoint(TimeSpan.Zero, cancellationToken);

    /// <summary>
    /// The code that leads the message of every operation refused because the database is
    /// offline (#1243).
    /// </summary>
    internal const string OfflineCode = "COHDBK002";

    /// <summary>
    /// Gets whether a failed durable flush of either file set took the database offline.
    /// </summary>
    internal bool IsOffline => OfflineError is not null;

    /// <summary>
    /// Gets the storage error that took the database offline, or null while it is online. Each
    /// file set's <see cref="Assimalign.Cohesion.Database.Storage.Storage.OnOffline"/> takes the other offline the moment it goes
    /// offline; reading the state takes both offline too, a backstop that costs nothing once
    /// they are.
    /// </summary>
    internal StorageOfflineException? OfflineError
    {
        get
        {
            var error = _storage.OfflineError ?? _catalogStorage.OfflineError;
            if (error is not null)
            {
                _storage.TakeOffline(error);
                _catalogStorage.TakeOffline(error);
            }

            return error;
        }
    }

    /// <summary>
    /// Refuses an operation on an offline database with <see cref="DatabaseOfflineException"/>
    /// (<see cref="OfflineCode"/>): every operation, in process and over the wire server, until
    /// the database is reopened.
    /// </summary>
    /// <exception cref="DatabaseOfflineException">The database is offline.</exception>
    internal void ThrowIfOffline()
    {
        if (OfflineError is { } error)
        {
            throw DatabaseOfflineException.Create(OfflineCode, Name, error);
        }
    }

    /// <summary>
    /// Translates a failure the storage's offline state caused into the coded refusal
    /// (<see cref="DatabaseOfflineException"/>), or into
    /// <see cref="DatabaseTransactionCommitUnconfirmedException"/> when a storage commit record
    /// was written before its flush failed (<see cref="StorageOfflineException.CommitRecordWritten"/>),
    /// so the work may survive the reopen. An unconfirmed commit that already has its own type is
    /// returned unchanged, and so is any other failure.
    /// </summary>
    /// <param name="error">The failure to translate.</param>
    /// <returns>The translated failure, or <paramref name="error"/> itself.</returns>
    internal Exception TranslateOffline(Exception error)
    {
        if (error is DatabaseOfflineException or DatabaseTransactionCommitUnconfirmedException or TransactionCommitUnconfirmedException
            || StorageOfflineException.Find(error) is not { } offline)
        {
            return error;
        }

        return offline.CommitRecordWritten
            ? DatabaseTransactionCommitUnconfirmedException.Create(OfflineCode, Name, offline)
            : DatabaseOfflineException.Create(OfflineCode, Name, OfflineError ?? offline);
    }

    /// <inheritdoc />
    public ValueTask<IDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        cancellationToken.ThrowIfCancellationRequested();

        var executor = new KeyValueOperationExecutor(Name, _catalog, _storage, _primaryIndex);
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

        // An offline database closes without writing anything (#1243): both file sets are
        // taken offline, so the coordinator's aborts undo nothing, no registration is saved,
        // and neither storage flushes at its close.
        bool offline = OfflineError is not null;

        // The coordinator first: the manager aborts every still-active logical
        // transaction (undoing its stamps through the version store's ledger)
        // while the storage is still open. Synchronous over the ValueTask by
        // design — the in-process implementations complete synchronously.
        // Registrations re-export after the aborts and before the storages close.
        // The storages close even when the coordinator reports a writer whose undo
        // still failed: it kept that writer in flight in the data storage, so the
        // close does not truncate the journal recovery classifies the writer from
        // (#1226).
        try
        {
            _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (!offline && !IsOffline)
            {
                SaveIndexRegistrationsIfChanged();
            }
        }
        finally
        {
            try
            {
                _storage.Dispose();
            }
            finally
            {
                _catalogStorage.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // See Dispose: an offline database closes without writing anything (#1243).
        bool offline = OfflineError is not null;
        try
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
            if (!offline && !IsOffline)
            {
                SaveIndexRegistrationsIfChanged();
            }
        }
        finally
        {
            try
            {
                await _storage.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _catalogStorage.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Keeps the area's pairing error at the engine boundary while the shared
    /// coordinator owns the current statement bracket.
    /// </summary>
    private sealed class StatementTransactionSource : IStorageTransactionSource
    {
        private readonly TransactionCoordinator _coordinator;

        /// <summary>
        /// Initializes a new instance of the <see cref="StatementTransactionSource"/> class.
        /// </summary>
        /// <param name="coordinator">The coordinator that owns each transaction's current statement bracket.</param>
        public StatementTransactionSource(TransactionCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        /// <inheritdoc />
        public IStorageTransaction GetStorageTransaction(ITransactionContext context)
        {
            if (_coordinator.TryGetStorageTransaction(context, out var transaction))
            {
                return transaction;
            }

            throw new DatabaseException(
                $"Transaction {context.Sequence} has no statement bracket applying on this database.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
