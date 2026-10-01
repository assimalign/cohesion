using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
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
    private readonly TransactionCoordinator _coordinator;
    private readonly IIndexManager _indexManager;
    private readonly SqlSchemaProvisioner _schemaProvisioner;
    private bool _disposed;

    internal SqlDatabaseInstance(string name, IDatabaseEngine engine, SqlStorage storage, SqlStorage catalogStorage,
        bool recover = false, Collation? defaultCollation = null)
    {
        Name = name;
        Engine = engine;
        _storage = storage;
        _catalogStorage = catalogStorage;
        _catalog = defaultCollation is null
            ? SqlCatalog.Open(catalogStorage)
            : SqlCatalog.Open(catalogStorage, defaultCollation);

        // The data-storage format gate, before any engine component touches the
        // data file set: an existing database must be on exactly this engine's
        // format (recovery's scrub, index purge and checkpoint below would
        // otherwise run with the wrong key encoding), and a new one is born on it.
        if (recover)
        {
            ThrowIfFormatIsNotCurrent();
        }
        else
        {
            // Synchronous over the ValueTask by design: catalog writes complete
            // synchronously and instance construction is a synchronous path.
            _catalog.SetRecordSpaceFormatVersionAsync(SqlRowCodec.RecordSpaceFormatVersion)
                .AsTask().GetAwaiter().GetResult();
        }

        _coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, new SqlTransactionRecordSpace(storage));

        // Re-attach the persisted secondary indexes before recovery: the
        // open-time scrub must be able to purge unproven writers' entries out of
        // every tree. Index pages live in the SAME data file set as rows (the
        // transactional page surface), so storage recovery has already replayed
        // them by the time the manager attaches.
        _indexManager = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = storage,
            TransactionSource = new StatementTransactionSource(_coordinator),
            LockManager = _coordinator.LockManager,
            ExistingIndexes = _catalog.GetIndexRegistrations(),
        });
        _schemaProvisioner = new SqlSchemaProvisioner(this, _catalog);

        if (recover)
        {
            // Reopened storage: classify the recovered journal, purge unproven
            // writers from the record space AND the secondary indexes, then
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
    }

    /// <summary>
    /// Refuses an existing data storage on any format but this engine's own. The
    /// engine has no upgrade path (owner decision of 2026-10-01; upgrades are
    /// #1152): an older database must be recreated, and a newer one belongs to
    /// the engine that wrote it. Runs as soon as the catalog is open, before the
    /// transaction coordinator, the index manager or recovery read or write the
    /// data file set, so a refused database is left exactly as it was found and
    /// the engine that wrote it can still open it.
    /// </summary>
    /// <exception cref="DatabaseException">The data-storage format is not <see cref="SqlRowCodec.RecordSpaceFormatVersion"/>.</exception>
    private void ThrowIfFormatIsNotCurrent()
    {
        int version = _catalog.RecordSpaceFormatVersion;
        int current = SqlRowCodec.RecordSpaceFormatVersion;

        if (version == current)
        {
            return;
        }

        string remedy = version < current
            ? "This engine does not upgrade databases written in an older format: recreate the database with this " +
              "engine and reload its data, exporting it first with the engine that wrote it " +
              "(on-disk format upgrades are tracked by assimalign/cohesion#1152)."
            : "The database was written by a newer engine; open it with that engine.";

        throw new DatabaseException(
            $"Database '{Name}' uses data-storage format {version}, but this engine supports only format {current}. {remedy}");
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
    /// Gets the database's index manager (the live B+Tree directory over the data
    /// file set), for the executor, the engine's background workers, and tests.
    /// </summary>
    internal IIndexManager IndexManager => _indexManager;

    /// <summary>
    /// Persists the index manager's current registrations when they drifted from
    /// the stored set — root page ids change on splits, so this runs at the
    /// engine's persistence points (checkpoint passes and disposal) in addition
    /// to index DDL itself.
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
    /// Gets the database's transaction coordinator (the MVCC composition sessions
    /// bind to), for the engine's background workers and tests.
    /// </summary>
    internal TransactionCoordinator Coordinator => _coordinator;

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

        var executor = new SqlQueryExecutor(_storage, _catalog, _indexManager);
        var session = new SqlDatabaseSession(this, _coordinator, executor);

        return new ValueTask<IDatabaseSession>(session);
    }

    /// <summary>
    /// Creates the provisioner's private session. Only this path stamps schema ownership
    /// and authorizes schema-owned DDL; ordinary sessions have no ownership bypass.
    /// </summary>
    internal IDatabaseSession CreateSchemaSession(string provisioningSchema, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var executor = new SqlQueryExecutor(_storage, _catalog, _indexManager);
        return new SqlDatabaseSession(this, _coordinator, executor, provisioningSchema);
    }

    /// <inheritdoc />
    public ValueTask<SchemaMigrationResult> ApplySchemaAsync(
        CompiledSchema schema,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _schemaProvisioner.ApplyAsync(schema, cancellationToken);
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
        // implementations complete synchronously. Registrations re-export after
        // the aborts (a rollback never moves roots, but the order costs nothing)
        // and before the storages close.
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
        /// <param name="coordinator">The transaction coordinator that owns the current statement bracket.</param>
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
