using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.KeyValuePair;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.KeyValuePair.Catalog;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// A key-value-model database: an ordered key space with point and range operations and
/// per-entry etags for conditional writes.
/// </summary>
/// <remarks>
/// <para>
/// Keys and values are opaque byte sequences; keys order by unsigned lexicographic byte
/// comparison, making prefix and range scans meaningful. The typed members are conveniences over
/// the session's typed-request seam: each executes the corresponding <see cref="KeyValueRequest"/>
/// on the given session, so visibility and conflict semantics are identical to executing the
/// request directly. Conditional misses (compare-and-swap) are first-class outcomes; concurrency
/// conflicts surface as the root's retryable transaction exceptions
/// (<see cref="DatabaseTransactionAbortedException"/> /
/// <see cref="DatabaseTransactionDeadlockException"/>).
/// </para>
/// <para>
/// A database composes the data storage, the dedicated catalog storage, the catalog opened over
/// it, the transaction coordinator (the per-database MVCC composition of transaction manager,
/// lock manager and version store every session binds to) and the <b>primary key index</b>: the
/// unique B+Tree over the key space that is the model's primary structure (key → packed entry
/// location; the index-primary composition).
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of
/// <see cref="DatabaseInstance"/> with an internal constructor, replacing the former
/// <c>IKeyValueDatabase</c> interface and its internal implementation; the engine creates and
/// opens it. The base owns the name, the owning engine (re-exposed typed with <c>new</c>) and the
/// disposed flag. Disposing it outside the engine closes it for every session; once the close
/// ends the engine forgets it (owner decision 33 of 2026-10-06, #1289), and
/// <see cref="KeyValueDatabaseEngine.OpenDatabaseAsync(DatabaseName, CancellationToken)"/> opens
/// it again from its files, with its entries, as a new instance. Until then its workers skip it,
/// so the engine stays <see cref="EngineState.Running"/>.
/// </para>
/// </remarks>
public sealed class KeyValueDatabase : DatabaseInstance
{
    private readonly KeyValueDatabaseEngine _engine;
    private readonly KeyValueStorage _storage;
    private readonly KeyValueStorage _catalogStorage;
    private readonly KeyValueCatalog _catalog;
    private readonly TransactionCoordinator _coordinator;
    private readonly BTreeIndexManager _indexManager;
    private readonly BTreeIndex _primaryIndex;

    internal KeyValueDatabase(DatabaseName name, KeyValueDatabaseEngine engine, KeyValueStorage storage, KeyValueStorage catalogStorage, bool recover = false)
        : base(name, engine)
    {
        _engine = engine;
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

        // A wait for a lock of the database ends when it goes offline (#1268 review): an offline
        // database undoes nothing, so a writer that holds a lock keeps it until the reopen, and a
        // writer queued behind it would otherwise wait that long. The catalog file set's hook takes
        // the data set offline, whose hook ends the waits, so both paths end them.
        _storage.OnOffline = error =>
        {
            _catalogStorage.TakeOffline(error);
            _coordinator.AbandonLockWaits(error);
        };
        if (OfflineError is { } offlineAtOpen)
        {
            _coordinator.AbandonLockWaits(offlineAtOpen);
        }

        // A deferred undo is retried on its own backoff, from about 100 ms up to the
        // maintenance interval, and the purge worker wakes for it (#1226).
        _coordinator.DeferredUndoRetryLimit = engine.EngineOptions.MaintenanceInterval;
        _coordinator.DeferredUndoRetryDelay = engine.EngineOptions.DeferredUndoRetryDelay;
        _coordinator.OnUndoDeferred = engine.UndoDeferredSignal.Set;

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
                TransactionSource = ResolveStatementBracket,
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
    private BTreeIndex EnsurePrimaryIndex()
    {
        if (_indexManager.TryGetIndex(KeyValueOperationExecutor.KeySpaceObjectId, KeyValueOperationExecutor.PrimaryIndexName, out var existing))
        {
            return existing;
        }

        // Synchronous over the ValueTask by design: the in-process
        // implementations complete synchronously and instance open is a
        // synchronous path (the SqlDatabase precedent).
        var context = _coordinator.BeginAsync(IsolationLevel.Snapshot)
            .AsTask().GetAwaiter().GetResult();

        BTreeIndex index;
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
        _catalog.SaveIndexRegistrationsAsync(_indexManager.ExportRegistrations())
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
    private static void ThrowIfFormatIsNotCurrent(string name, KeyValueCatalog catalog)
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

    /// <summary>
    /// Gets the key-value engine that owns this database.
    /// </summary>
    public new KeyValueDatabaseEngine Engine => _engine;

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
    internal KeyValueCatalog Catalog => _catalog;

    /// <summary>
    /// Gets the database's index manager (the live B+Tree directory over the data
    /// file set), for the engine's background workers and tests.
    /// </summary>
    internal BTreeIndexManager IndexManager => _indexManager;

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
        var current = _indexManager.ExportRegistrations();
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
    /// Gets whether the database's close has started: by the engine, or by a holder of the
    /// database (a session's <see cref="KeyValueDatabaseSession.Database"/> is the same instance).
    /// The engine keeps a database its holder is closing registered until the close ends, then
    /// forgets it; its workers skip it meanwhile.
    /// </summary>
    internal bool IsClosed => IsDisposed;

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
        if (GetOfflineRefusal() is { } refusal)
        {
            throw refusal;
        }
    }

    /// <summary>
    /// Gets the coded refusal (<see cref="OfflineCode"/>) of an operation on the database while it
    /// is offline, or null while it is online: the vocabulary the root transaction base reads
    /// before a commit or rollback, and before each kernel rollback, which an offline database skips.
    /// </summary>
    /// <returns>The refusal, or null.</returns>
    internal DatabaseOfflineException? GetOfflineRefusal()
        => OfflineError is { } error ? DatabaseOfflineException.Create(OfflineCode, Name, error) : null;

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

    /// <summary>
    /// Translates the transaction kernel's unconfirmed commit into the area root's, its message led
    /// by <see cref="OfflineCode"/> as on every other unconfirmed path (owner decision 24 of
    /// 2026-10-06, #1272): an explicit transaction's commit and an auto-commit command's.
    /// </summary>
    /// <param name="error">The kernel's unconfirmed commit, kept as the inner exception.</param>
    /// <returns>The exception to throw.</returns>
    internal DatabaseTransactionCommitUnconfirmedException CreateUnconfirmedCommit(TransactionCommitUnconfirmedException error)
        => DatabaseTransactionCommitUnconfirmedException.Create(OfflineCode, Name, error);

    /// <summary>
    /// Creates a new lightweight key-value session scoped to this database.
    /// </summary>
    /// <param name="cancellationToken">Observed before the session is created.</param>
    /// <returns>A new session.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBK002</c>, #1243).</exception>
    public new async ValueTask<KeyValueDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
        => (KeyValueDatabaseSession)await base.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfOffline();

        var executor = new KeyValueOperationExecutor(Name, _catalog, _storage, _primaryIndex);
        var session = new KeyValueDatabaseSession(this, _coordinator, executor);

        return new ValueTask<DatabaseSession>(session);
    }

    // ── Typed model surface (conveniences over the typed-request seam) ──

    /// <summary>
    /// Reads the entry for a key.
    /// </summary>
    /// <param name="session">The session the read executes in.</param>
    /// <param name="key">The key to read.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The entry, or null when the key has no visible, live entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The session does not belong to this database, the session is closed, or its transaction refuses commands (<c>COHDBK001</c>).</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBK002</c>, #1243).</exception>
    public async ValueTask<KeyValueEntry?> GetAsync(KeyValueDatabaseSession session, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Writes the entry for a key, inserting or replacing, optionally conditional.
    /// </summary>
    /// <param name="session">The session the write executes in.</param>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="options">Write conditions (insert-only, compare-and-swap), or null for an unconditional upsert.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The write outcome: whether it applied, and the new (or current) etag.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The session does not belong to this database, the session is closed, or its transaction refuses commands (<c>COHDBK001</c>).</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBK002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">Thrown when a concurrently committed transaction changed the key (first-updater-wins; retryable).</exception>
    /// <exception cref="DatabaseTransactionDeadlockException">The command was chosen as a deadlock victim (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An auto-commit command's commit record could not be confirmed durable.</exception>
    public async ValueTask<KeyValuePutResult> PutAsync(KeyValueDatabaseSession session, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, KeyValuePutOptions? options = null, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Deletes the entry for a key, optionally conditional.
    /// </summary>
    /// <param name="session">The session the delete executes in.</param>
    /// <param name="key">The key to delete.</param>
    /// <param name="expectedETag">The etag the current entry must carry for the delete to apply, or null for an unconditional delete.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when an entry was deleted; false when none was visible or the condition did not hold.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The session does not belong to this database, the session is closed, or its transaction refuses commands (<c>COHDBK001</c>).</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBK002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">Thrown when a concurrently committed transaction changed the key (first-updater-wins; retryable).</exception>
    /// <exception cref="DatabaseTransactionDeadlockException">The command was chosen as a deadlock victim (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An auto-commit command's commit record could not be confirmed durable.</exception>
    public async ValueTask<bool> TryDeleteAsync(KeyValueDatabaseSession session, ReadOnlyMemory<byte> key, long? expectedETag = null, CancellationToken cancellationToken = default)
    {
        var result = await RequireOwnSession(session).ExecuteAsync(new KeyValueDeleteRequest(key, expectedETag), cancellationToken).ConfigureAwait(false);
        return result.AffectedCount > 0;
    }

    /// <summary>
    /// Probes whether a key has a visible, live entry.
    /// </summary>
    /// <param name="session">The session the probe executes in.</param>
    /// <param name="key">The key to probe.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the key has a visible entry; otherwise false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The session does not belong to this database, the session is closed, or its transaction refuses commands (<c>COHDBK001</c>).</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBK002</c>, #1243).</exception>
    public async ValueTask<bool> ExistsAsync(KeyValueDatabaseSession session, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Streams entries in ascending key order.
    /// </summary>
    /// <param name="session">The session the scan executes in.</param>
    /// <param name="options">Scan bounds and limits, or null to scan everything.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of visible, live entries in ascending key order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">The session does not belong to this database, the session is closed, or its transaction refuses commands (<c>COHDBK001</c>).</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBK002</c>, #1243).</exception>
    public async IAsyncEnumerable<KeyValueEntry> ScanAsync(KeyValueDatabaseSession session, KeyValueScanOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
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

    private KeyValueDatabaseSession RequireOwnSession(KeyValueDatabaseSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!ReferenceEquals(session.Database, this))
        {
            throw new DatabaseException("The session does not belong to this key-value database.");
        }

        return session;
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
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
    protected override async ValueTask DisposeAsyncCore()
    {
        // See DisposeCore: an offline database closes without writing anything (#1243).
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
    /// Resolves the index manager's storage transaction for a context: the statement bracket
    /// the shared coordinator owns, with the area's pairing error at the engine boundary.
    /// </summary>
    /// <param name="context">The transaction an index mutation belongs to.</param>
    /// <returns>The context's current statement bracket.</returns>
    /// <exception cref="DatabaseException">No statement of the transaction is applying on this database.</exception>
    private StorageTransaction ResolveStatementBracket(TransactionContext context)
    {
        if (_coordinator.TryGetStorageTransaction(context, out var transaction))
        {
            return transaction;
        }

        throw new DatabaseException(
            $"Transaction {context.Sequence} has no statement bracket applying on this database.");
    }
}
