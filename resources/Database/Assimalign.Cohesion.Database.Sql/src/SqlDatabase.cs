using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// A SQL-model database: relational tables and their indexes, queried and changed through the
/// sessions it creates (<see cref="CreateSessionAsync"/>), and provisioned from a compiled schema
/// (<see cref="ApplySchemaAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// A database composes the data storage, the dedicated catalog storage, the catalog opened over
/// it (<see cref="SqlCatalog"/>), and the transaction coordinator: the per-database MVCC
/// composition (transaction manager, lock manager, version store) every session binds to.
/// </para>
/// <para>
/// <b>Schema provisioning</b> belongs to the SQL model (owner decisions 50 and 53 of 2026-10-09).
/// The engine's builder applies cheap schema steps before returning and reserves populated tables
/// for its worker to finish persisted data-scaling steps. <see cref="ApplySchemaAsync"/> applies a
/// compiled schema inline for tools, Studio and tests. The root <see cref="DatabaseInstance"/>
/// exposes model-neutral progress without a schema capability API.
/// </para>
/// <para>
/// <b>Closed by its holder.</b> Disposing the database closes it for every session. Once the
/// close ends the engine forgets it (owner decision 33 of 2026-10-06, #1289), and
/// <see cref="SqlDatabaseEngine.OpenDatabaseAsync(DatabaseName, CancellationToken)"/> opens it
/// again from its files, with its data, as a new instance; until then the engine's workers skip
/// it, so the engine stays <see cref="EngineState.Running"/> and its server keeps serving the
/// engine's other databases.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed leaf of
/// <see cref="DatabaseInstance"/> with an internal constructor, replacing the former
/// <c>ISqlDatabase</c> interface and its internal implementation; the engine creates and opens
/// it. The base owns the name, the owning engine (re-exposed typed with <c>new</c>) and the
/// disposed flag.
/// </para>
/// </remarks>
public sealed class SqlDatabase : DatabaseInstance
{
    private readonly SqlStorage _storage;
    private readonly SqlStorage _catalogStorage;
    private readonly SqlCatalog _catalog;
    private readonly TransactionCoordinator _coordinator;
    private readonly BTreeIndexManager _indexManager;
    private readonly SqlSchemaProvisioner _schemaProvisioner;

    // Internal diagnostics/fault-injection seam for this project's own tests.
    internal SqlSchemaProvisioner SchemaProvisioner => _schemaProvisioner;
    internal SqlDdlInterlock DdlInterlock { get; } = new();
    internal SqlTableReclamation TableReclamation { get; }
    internal void ReportProvisioning(DatabaseProvisioningProgress? progress) => SetProvisioningProgress(progress);

    internal async ValueTask ResumeProvisioningAsync(CancellationToken cancellationToken)
    {
        if (_catalog.PendingMigration is not { } pending)
        {
            return;
        }
        var schema = SqlCompiledSchemaSerializer.Deserialize(pending.CanonicalDocument);
        if (!string.Equals(schema.Hash, pending.ContentHash, StringComparison.Ordinal))
        {
            throw new SqlSchemaMigrationException($"{SqlProvisioningCodes.PolicyRefused}: pending migration target hash is invalid.");
        }
        var declaration = _engine.FindDeclaration(Name);
        if (declaration?.Mode == SqlProvisioningMode.Verify)
        {
            await _schemaProvisioner.VerifyAsync(schema, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (declaration?.Schema is { } declared && !string.Equals(declared.Hash, schema.Hash, StringComparison.Ordinal))
            {
                throw new SqlSchemaMigrationException($"{SqlProvisioningCodes.PolicyRefused}: database '{Name}' has a different pending schema. " +
                    "Finish its recorded migration before replacing the declaration.");
            }
            await _schemaProvisioner.ApplyAsync(schema, cancellationToken, declaration?.RunMigrationsInline != true).ConfigureAwait(false);
        }
    }
    private readonly SqlBoundTableCache _definitions;
    private readonly SqlDatabaseEngine _engine;

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
    internal SqlDatabase(DatabaseName name, SqlDatabaseEngine engine, SqlStorage storage, SqlStorage catalogStorage,
        SqlCatalog catalog, bool recover, CancellationToken cancellationToken = default)
        : base(name, engine)
    {
        _engine = engine;
        _storage = storage;
        _catalogStorage = catalogStorage;
        _catalog = catalog;

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

        if (recover)
        {
            // The engine gates the catalog before it opens the data file set; the
            // re-check keeps the invariant local to the instance, because
            // recovery's scrub, index purge and checkpoint below would otherwise
            // run with the wrong key encoding.
            ThrowIfFormatIsNotCurrent(Name, _catalog.RecordSpaceFormatVersion);
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
        _definitions = new SqlBoundTableCache(_catalog, new SqlFunctionEnvironment(engine.Functions, name, engine.Types), cancellationToken);
        try
        {
            _definitions.BindCatalog(cancellationToken);
        }
        catch (DatabaseException exception)
        {
            throw new DatabaseException($"Database '{name}' cannot be opened. {exception.Message}", exception.Diagnostics, exception);
        }

        _coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, new SqlTransactionRecordSpace(storage))
        {
            // A deferred undo is retried on its own backoff, from about 100 ms up to the
            // maintenance interval, and the purge worker wakes for it (#1226).
            DeferredUndoRetryLimit = engine.EngineOptions.MaintenanceInterval,
            DeferredUndoRetryDelay = engine.EngineOptions.DeferredUndoRetryDelay,
            OnUndoDeferred = engine.UndoDeferredSignal.Set,
        };

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
                TransactionSource = ResolveStatementBracket,
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
        TableReclamation = new SqlTableReclamation(this);
        _schemaProvisioner = new SqlSchemaProvisioner(this, _catalog);
        engine.BeforeProvisioning?.Invoke(this);

        if (recover)
        {
            // Reopened storage: classify the recovered journal, purge unproven
            // writers from the record space AND the secondary indexes, then
            // checkpoint (the open-time checkpoint the storage strategy
            // deferred — it must come last, because truncation destroys the
            // lifecycle records classification reads).
            try
            {
                var plan = _coordinator.AnalyzeAndScrub(cancellationToken);
                if (plan.Aborted.Count > 0)
                {
                    using var scrub = _storage.BeginTransaction();
                    _indexManager.PurgeWritersAsync(scrub, plan.Aborted, cancellationToken)
                        .AsTask().GetAwaiter().GetResult();
                    scrub.Commit();
                }
                _coordinator.CompleteRecovery(cancellationToken);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Partial scrub brackets may have committed. Closing must preserve the
                // classification journal, never checkpoint away the unprocessed writers.
                _storage.TakeOffline(StorageOfflineCause.RecoveryInterrupted,
                    $"Database '{Name}' closed before transaction recovery completed; its journal is retained for the next open.", failure);
                _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
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
    /// <param name="version">The format version recorded in the database's catalog.</param>
    /// <exception cref="SqlDataStorageFormatException">The data-storage format is not <see cref="SqlRowCodec.RecordSpaceFormatVersion"/>.</exception>
    internal static void ThrowIfFormatIsNotCurrent(string name, int version)
    {
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
    /// (<see cref="SqlStorageStrategy.CreateStorage"/> throws when storage
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
                "SqlStorageStrategy.CreateStorage must return new, empty storage.");
        }

        // Synchronous over the ValueTask by design: catalog writes complete
        // synchronously and instance construction is a synchronous path.
        // The marker is the final creation commit: every current-format database must
        // already contain dbo. A crash before it remains an interrupted format-1 creation.
        CompleteCreation(_catalog);
    }

    // Admit only records creation itself can commit before its marker: collation,
    // the initial counter, and dbo. An explicit format-1 marker is not an interruption.
    internal static bool IsInterruptedCreation(SqlStorage storage, SqlCatalog catalog)
    {
        if (catalog.RecordSpaceFormatVersion != 1 || catalog.Namespaces.Any(schema => schema.Name != SqlNamespaces.DefaultName))
        {
            return false;
        }
        using var iterator = storage.GetUnitIterator();
        while (iterator.MoveNext())
        {
            var reader = new DatabaseKeyReader(iterator.Current.Data.Span);
            int kind = reader.ReadInt32();
            if (kind == 2)
            {
                if (reader.ReadInt64() != 1 || reader.ReadInt64() > 2) { return false; }
            }
            else if (kind is not (7 or 15))
            {
                return false;
            }
        }
        return true;
    }

    internal static void CompleteCreation(SqlCatalog catalog)
    {
        if (catalog.Namespaces.Count == 0)
        {
            catalog.CreateSchemaAsync(new SqlCatalogNamespace(SqlNamespaces.DefaultName))
                .AsTask().GetAwaiter().GetResult();
        }
        catalog.SetRecordSpaceFormatVersionAsync(SqlRowCodec.RecordSpaceFormatVersion)
            .AsTask().GetAwaiter().GetResult();
    }

    internal static bool IsEmptyDataStorage(SqlStorage storage)
    {
        // A crash can retain the header before the initial empty data page was written.
        // More pages, or even one record in the shared initial page, are not a new data set.
        if (storage.PageManager.PageCount > 2) { return false; }
        using var iterator = storage.GetUnitIterator();
        return !iterator.MoveNext();
    }

    /// <summary>
    /// Gets the SQL engine that owns this database.
    /// </summary>
    public new SqlDatabaseEngine Engine => _engine;

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
    internal SqlCatalog Catalog => _catalog;

    /// <summary>
    /// Gets the database's index manager (the live B+Tree directory over the data
    /// file set), for the executor, the engine's background workers, and tests.
    /// </summary>
    internal BTreeIndexManager IndexManager => _indexManager;

    /// <summary>
    /// Gets the database's bound table versions — every persisted CHECK and DEFAULT, parsed
    /// once — for the executor and tests.
    /// </summary>
    internal SqlBoundTableCache Definitions => _definitions;

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
    internal const string OfflineCode = "COHSQLT004";

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
        if (GetOfflineRefusal() is { } refusal)
        {
            throw refusal;
        }
    }

    /// <summary>
    /// Gets the coded refusal of an operation on the offline database (<see cref="OfflineCode"/>),
    /// or null while it is online: what <see cref="ThrowIfOffline"/> throws, for the
    /// transaction base's offline refusal.
    /// </summary>
    /// <returns>The refusal, or null.</returns>
    internal DatabaseOfflineException? GetOfflineRefusal()
        => OfflineError is { } error ? DatabaseOfflineException.Create(OfflineCode, Name, error) : null;

    /// <summary>
    /// Gets whether the database's close has started: by the engine, or by a holder of the
    /// database (<c>await using var database = await engine.CreateDatabaseAsync(...)</c>, or a
    /// session's <see cref="SqlDatabaseSession.Database"/>). The engine keeps a database its holder
    /// is closing registered until the close ends, then forgets it; its workers skip it meanwhile.
    /// </summary>
    internal bool IsClosed => IsDisposed;

    /// <summary>
    /// Translates a failure the storage's offline state caused into the coded refusal
    /// (<see cref="DatabaseOfflineException"/>), or into
    /// <see cref="DatabaseTransactionCommitUnconfirmedException"/> when the work may survive the
    /// reopen: a storage commit record was written before its flush failed
    /// (<see cref="StorageOfflineException.CommitRecordWritten"/>), or the operation is a
    /// self-committing statement that had already committed a catalog change a reopened database
    /// shows. Both lead with <see cref="OfflineCode"/>. An unconfirmed commit that already has its
    /// own type is returned unchanged, and so is any other failure.
    /// </summary>
    /// <param name="error">The failure to translate.</param>
    /// <param name="selfCommitted">
    /// True for a self-committing statement (DDL) that committed at least one catalog change a
    /// reopened database shows (a published, altered or removed definition) before the failure:
    /// that part survives the reopen, so it is never reported as refused. A DDL statement that met
    /// the offline storage before it committed such a change passes false and is reported as
    /// refused, exactly as any other statement (#1272), even when it had durably committed work
    /// nothing can reach: a reserved table identity, or index trees no catalog entry describes.
    /// </param>
    /// <returns>The translated failure, or <paramref name="error"/> itself.</returns>
    internal Exception TranslateOffline(Exception error, bool selfCommitted = false)
    {
        if (error is DatabaseOfflineException or DatabaseTransactionCommitUnconfirmedException or TransactionCommitUnconfirmedException
            || StorageOfflineException.Find(error) is not { } offline)
        {
            return error;
        }

        return offline.CommitRecordWritten || selfCommitted
            ? DatabaseTransactionCommitUnconfirmedException.Create(OfflineCode, Name, offline)
            : DatabaseOfflineException.Create(OfflineCode, Name, OfflineError ?? offline);
    }

    /// <summary>
    /// Translates the transaction kernel's unconfirmed commit into the area root's, its message led
    /// by <see cref="OfflineCode"/> as on every other unconfirmed path (owner decision 24 of
    /// 2026-10-06, #1272): an explicit transaction's commit and an auto-commit statement's.
    /// </summary>
    /// <param name="error">The kernel's unconfirmed commit, kept as the inner exception.</param>
    /// <returns>The exception to throw.</returns>
    internal DatabaseTransactionCommitUnconfirmedException CreateUnconfirmedCommit(TransactionCommitUnconfirmedException error)
        => DatabaseTransactionCommitUnconfirmedException.Create(OfflineCode, Name, error);

    /// <summary>
    /// Creates a new lightweight SQL session scoped to this database.
    /// </summary>
    /// <param name="cancellationToken">Observed before the session is created.</param>
    /// <returns>A new session.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the session was created.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHSQLT004</c>, #1243).</exception>
    public new async ValueTask<SqlDatabaseSession> CreateSessionAsync(CancellationToken cancellationToken = default)
        => (SqlDatabaseSession)await base.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override ValueTask<DatabaseSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfOffline();

        var executor = new SqlQueryExecutor(_storage, _catalog, _indexManager, _definitions);
        var session = new SqlDatabaseSession(this, _coordinator, executor, _engine.ParserOptions);

        return new ValueTask<DatabaseSession>(session);
    }

    /// <summary>
    /// Creates the provisioner's private session. Only this path stamps schema ownership
    /// and authorizes schema-owned DDL; ordinary sessions have no ownership bypass.
    /// </summary>
    internal SqlDatabaseSession CreateSchemaSession(string provisioningSchema, CancellationToken cancellationToken,
        TransactionSequence? ddlLockOwner = null)
    {
        ThrowIfDisposed();
        ThrowIfOffline();
        cancellationToken.ThrowIfCancellationRequested();
        var executor = new SqlQueryExecutor(_storage, _catalog, _indexManager, _definitions);
        return new SqlDatabaseSession(this, _coordinator, executor, _engine.ParserOptions, provisioningSchema, ddlLockOwner);
    }

    /// <summary>
    /// Diffs the database against a compiled schema and applies the difference: the schema's
    /// tables, columns, keys, indexes and constraints, created and changed on a private session
    /// that stamps them <see cref="DatabaseObjectOwner.Schema"/>-owned, then the schema's hash and
    /// canonical document recorded in the catalog.
    /// </summary>
    /// <param name="schema">The desired schema; its name must be the database's.</param>
    /// <param name="cancellationToken">Observed before anything runs and between steps.</param>
    /// <returns>The migration result; <see cref="SqlSchemaMigrationResult.WasAlreadyApplied"/> when nothing ran.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHSQLT004</c>, #1243).</exception>
    /// <exception cref="DatabaseObjectLockedException">
    /// The engine's builder declared the database with another schema: the declaration owns the
    /// database (owner decision 56 of 2026-10-09), so its schema changes through the declaration.
    /// Also thrown before changes when an ordinary session DDL statement, another apply,
    /// verification or deferred provisioning worker already owns admission. Contention never waits.
    /// </exception>
    /// <exception cref="SqlSchemaMigrationException">
    /// The schema targets another database, declares what the engine has no DDL for (principals),
    /// declares a domain the engine does not register with that base type (<c>COHSQLP005</c>: an
    /// imperative apply cannot add to the engine's frozen type catalog), would adopt an ad-hoc object
    /// (<c>COHSQLP005</c>), needs a destructive step it does not allow (<c>COHSQLP005</c>), or a step
    /// failed (<c>COHSQLP004</c>, after the completed reversible steps were compensated).
    /// </exception>
    /// <exception cref="SqlSchemaValidationException">A recorded schema document uses an unsupported format.</exception>
    /// <remarks>
    /// Kept public on the sealed leaf for tools, Studio and tests (owner decision 53 of
    /// 2026-10-09). An engine's builder applies each declared database's schema through the same
    /// provisioner while it builds. A database the builder declared with a schema accepts only that
    /// schema here: another one would be planned away, or refused as destructive, by the engine's
    /// next build. A database declared without a schema, and one the builder did not declare, accept
    /// any schema. The checks run in the order the root base's members use: disposal, the schema,
    /// the token, then the offline refusal, then the declaration.
    /// </remarks>
    public ValueTask<SqlSchemaMigrationResult> ApplySchemaAsync(SqlCompiledSchema schema, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(schema);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOffline();
        if (_engine.FindDeclaration(Name) is { Schema: { } declared } &&
            !string.Equals(declared.Hash, schema.Hash, StringComparison.Ordinal))
        {
            throw new DatabaseObjectLockedException(
                Name,
                Name,
                "APPLY SCHEMA",
                $"SQL engine '{_engine.Name}' declares database '{Name}' with a schema (SqlDatabaseBuilder.Schema), so " +
                "ApplySchemaAsync refuses another one: change the declaration and rebuild the engine instead.");
        }

        return _schemaProvisioner.ApplyAsync(schema, cancellationToken);
    }

    /// <summary>
    /// Verifies, without running any DDL, that a compiled schema is the one applied: the recorded
    /// hash and canonical document and the live schema-owned catalog all match it. The builder's
    /// <see cref="SqlProvisioningMode.Verify"/> path.
    /// </summary>
    /// <param name="schema">The declared schema.</param>
    /// <param name="cancellationToken">Observed before the comparison.</param>
    /// <returns>The result, which is always already applied.</returns>
    /// <exception cref="SqlSchemaMigrationException">The database drifted from the schema (<c>COHSQLP003</c>).</exception>
    /// <exception cref="SqlSchemaValidationException">A recorded schema document uses an unsupported format.</exception>
    internal ValueTask<SqlSchemaMigrationResult> VerifySchemaAsync(SqlCompiledSchema schema, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(schema);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOffline();
        return _schemaProvisioner.VerifyAsync(schema, cancellationToken);
    }

    /// <inheritdoc />
    protected override void DisposeCore()
    {
        TableReclamation.Stop();
        _schemaProvisioner.StopDeferredAsync().AsTask().GetAwaiter().GetResult();
        // An offline database closes without writing anything (#1243): both file sets are
        // taken offline, so the coordinator's aborts undo nothing and neither storage
        // flushes at its close.

        // The coordinator first: the manager aborts every still-active logical
        // transaction (rolling its paired bracket back) while the storage is
        // still open. Synchronous over the ValueTask by design — the in-process
        // implementations complete synchronously. Index registrations were committed
        // with their descriptions during DDL. The storages close even when the
        // coordinator reports a writer whose undo still failed: it kept that
        // writer in flight in the data storage, so the close does not truncate
        // the journal recovery classifies the writer from (#1226).
        try
        {
            _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        TableReclamation.Stop();
        await _schemaProvisioner.StopDeferredAsync().ConfigureAwait(false);
        // See DisposeCore: an offline database closes without writing anything (#1243).
        try
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
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
