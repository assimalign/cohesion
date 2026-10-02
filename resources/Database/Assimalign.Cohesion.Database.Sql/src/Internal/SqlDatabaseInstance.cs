using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

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
    private readonly SqlBoundTableCache _definitions;
    private readonly SqlDatabaseEngine _engine;
    private bool _disposed;

    /// <summary>
    /// Composes a database over its two file sets and the catalog the engine opened
    /// on the catalog file set.
    /// </summary>
    /// <param name="name">The database name.</param>
    /// <param name="engine">The owning engine.</param>
    /// <param name="storage">The data file set.</param>
    /// <param name="catalogStorage">The catalog file set.</param>
    /// <param name="catalog">The catalog opened over <paramref name="catalogStorage"/>.</param>
    /// <param name="recover">
    /// <see langword="true"/> for an existing database, which must already have passed
    /// <see cref="ThrowIfFormatIsNotCurrent"/> before its data file set was opened;
    /// <see langword="false"/> for a new one, which is born on this engine's format.
    /// </param>
    internal SqlDatabaseInstance(string name, SqlDatabaseEngine engine, SqlStorage storage, SqlStorage catalogStorage,
        ISqlCatalog catalog, bool recover)
    {
        Name = name;
        Engine = engine;
        _engine = engine;
        _storage = storage;
        _catalogStorage = catalogStorage;
        _catalog = catalog;

        if (recover)
        {
            // The engine gates the catalog before it opens the data file set; the
            // re-check keeps the invariant local to the instance, because
            // recovery's scrub, index purge and checkpoint below would otherwise
            // run with the wrong key encoding.
            ThrowIfFormatIsNotCurrent(Name, _catalog);
        }
        else
        {
            StampNewCatalog();
        }

        // Every database that reaches this point is on format 5, which (since format 4) stores
        // CHECK and DEFAULT definitions as canonical SQL; older formats were refused above, so
        // no definition here predates canonical storage.
        // Parse and bind every persisted CHECK and DEFAULT now, once, before anything else
        // touches the database: a definition that does not load fails the open, naming its
        // table, instead of failing an arbitrary later write. Writes reuse these bindings.
        _definitions = new SqlBoundTableCache(_catalog);
        try
        {
            _definitions.BindCatalog();
        }
        catch (DatabaseException exception)
        {
            throw new DatabaseException($"Database '{name}' cannot be opened. {exception.Message}", exception);
        }

        _coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, new SqlTransactionRecordSpace(storage));

        // Re-attach the persisted secondary indexes before recovery: the
        // open-time scrub must be able to purge unproven writers' entries out of
        // every tree. Index pages live in the SAME data file set as rows (the
        // transactional page surface), so storage recovery has already replayed
        // them by the time the manager attaches.
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
            // The format gate above vouches for the trees only through the
            // catalog's marker. The index manager checks each tree's own page
            // format as it attaches it, and a tree the marker does not describe —
            // a damaged root, or pages written by another engine build — fails the
            // open before recovery writes anything, never a read later.
            _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new SqlDataStorageFormatException(
                $"Database '{name}' uses data-storage format {_catalog.RecordSpaceFormatVersion}, but one of its index trees " +
                $"does not: {exception.Message}", exception);
        }
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
    /// Refuses an existing database on any data-storage format but this engine's
    /// own. The engine has no upgrade path (owner decision of 2026-10-01; upgrades
    /// are #1152): an older database must be dropped and recreated, and a newer one
    /// belongs to the engine that wrote it. The engine calls this on the catalog
    /// alone, before it opens the data file set, so a refused open never touches
    /// the data files. The catalog file set gets only what opening any storage
    /// does: a cleanly closed one is left byte-identical, and a crashed one gets
    /// the storage layer's format-agnostic physical recovery and keeps its journal
    /// (an untouched storage closes without writing), so the engine that wrote the
    /// database can still open it.
    /// </summary>
    /// <param name="name">The database name, for the message.</param>
    /// <param name="catalog">The database's catalog, opened on its catalog file set.</param>
    /// <exception cref="SqlDataStorageFormatException">The data-storage format is not <see cref="SqlRowCodec.RecordSpaceFormatVersion"/>.</exception>
    internal static void ThrowIfFormatIsNotCurrent(string name, ISqlCatalog catalog)
    {
        int version = catalog.RecordSpaceFormatVersion;
        int current = SqlRowCodec.RecordSpaceFormatVersion;

        if (version == current)
        {
            return;
        }

        // Version 1 is what a catalog without a marker reads as. Every released
        // engine stamped one when it created a database, so a missing marker
        // means a creation that stopped before the stamp or a pre-release build.
        string remedy = version switch
        {
            1 => "It has no format marker: its creation was interrupted, or it predates format markers. " +
                 "This engine does not upgrade or repair databases: drop the database (DropDatabaseAsync) and " +
                 "create it again, exporting any data first with the engine that wrote it " +
                 "(on-disk format upgrades are tracked by assimalign/cohesion#1152).",
            _ when version < current =>
                 "This engine does not upgrade databases written in an older format: export its data with the " +
                 "engine that wrote it, drop the database (DropDatabaseAsync) and create it again with this engine, " +
                 "then reload the data (on-disk format upgrades are tracked by assimalign/cohesion#1152).",
            _ => "The database was written by a newer engine; open it with that engine.",
        };

        throw new SqlDataStorageFormatException(
            $"Database '{name}' uses data-storage format {version}, but this engine supports only format {current}. {remedy}");
    }

    /// <summary>
    /// Writes this engine's format marker into a new database's catalog. The
    /// storage strategy contract makes a created catalog empty
    /// (<see cref="ISqlStorageStrategy.CreateStorage"/> throws when storage
    /// already exists); the check enforces it here too, because stamping an
    /// existing catalog would declare its older index keys current — the silent
    /// corruption the format gate exists to prevent.
    /// </summary>
    /// <exception cref="DatabaseException">The catalog already holds a format marker or tables.</exception>
    private void StampNewCatalog()
    {
        if (_catalog.RecordSpaceFormatVersion != 1 || _catalog.Tables.Count != 0)
        {
            throw new DatabaseException(
                $"Database '{Name}' cannot be created: its catalog storage already holds data-storage format " +
                $"{_catalog.RecordSpaceFormatVersion} and {_catalog.Tables.Count} table(s). " +
                "ISqlStorageStrategy.CreateStorage must return new, empty storage.");
        }

        // Synchronous over the ValueTask by design: catalog writes complete
        // synchronously and instance construction is a synchronous path.
        _catalog.SetRecordSpaceFormatVersionAsync(SqlRowCodec.RecordSpaceFormatVersion)
            .AsTask().GetAwaiter().GetResult();
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
    /// Gets the database's bound table versions — every persisted CHECK and DEFAULT, parsed
    /// once — for the executor and tests.
    /// </summary>
    internal SqlBoundTableCache Definitions => _definitions;

    /// <summary>
    /// Persists the index manager's current registrations when they differ from
    /// the stored set. A tree's root page stays fixed through splits (#1159), so
    /// outside index DDL this normally finds nothing to write; it runs at the
    /// engine's persistence points (checkpoint passes and disposal), in addition
    /// to index DDL itself, as a backstop.
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

        var executor = new SqlQueryExecutor(_storage, _catalog, _indexManager, _definitions);
        var session = new SqlDatabaseSession(this, _coordinator, executor, _engine.ParserOptions);

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
        var executor = new SqlQueryExecutor(_storage, _catalog, _indexManager, _definitions);
        return new SqlDatabaseSession(this, _coordinator, executor, _engine.ParserOptions, provisioningSchema);
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
