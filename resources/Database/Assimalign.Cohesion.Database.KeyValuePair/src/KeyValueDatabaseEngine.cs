using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;

namespace Assimalign.Cohesion.Database.KeyValuePair;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Key-value database engine that manages the lifecycle of key-value database
/// instances.
/// </summary>
/// <remarks>
/// <para>
/// The engine is a data machine: <see cref="Create"/> returns it fully operational —
/// the storage strategy is resolved and the engine-owned background workers
/// (write-ahead-log group-commit flusher, page write-back, checkpointer, version
/// purge, and the index-maintenance stub) are already pumping on dedicated threads
/// the engine spawned — and disposal is its one lifecycle transition: quiesce the
/// workers, flush each database according to its backing's durability policy and
/// close it. Each database is backed by a storage strategy managing two file sets
/// (data and <c>.catalog</c>); the strategy is file-based when
/// <see cref="KeyValueDatabaseEngineOptions.RootPath"/> is set and in-memory otherwise.
/// The journal is owned per-database through the storage layer, so there is no
/// engine-level write-ahead log.
/// </para>
/// <para>
/// <b>A database its holder closed.</b> A database disposed outside the engine (directly, or
/// through a session's <see cref="KeyValueDatabaseSession.Database"/>) is forgotten once its close
/// ends (owner decision 33 of 2026-10-06, #1289), so a later
/// <see cref="OpenDatabaseAsync(DatabaseName, CancellationToken)"/> opens it again from its files,
/// in memory as on disk, with its entries. Until the close ends the engine's workers skip it
/// (<see cref="IsOpen(KeyValueDatabase)"/> is false for it), so the engine stays
/// <see cref="EngineState.Running"/> and its server keeps serving its other databases.
/// </para>
/// <para>
/// <b>Declared databases</b> (B3 of the engine extensibility design). An engine built through
/// <see cref="CreateBuilder(string)"/> has opened, or created, every database its builder declared
/// (<see cref="KeyValueDatabaseEngineBuilder.AddDatabase(string)"/>) before the build returns. The
/// declaration owns each of them, so <see cref="DatabaseEngine.DropDatabaseAsync"/> refuses it with
/// <see cref="DatabaseObjectLockedException"/> (owner decision 56 of 2026-10-09).
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A sealed leaf of the root
/// <see cref="DatabaseEngine"/>: the base owns the name, the model, the worker pumps, the
/// state fold, the composition attach and freeze, the argument and disposed checks of every
/// public member, and the disposal order (servers, the worker pumps and the workers, then
/// <see cref="DisposeAsyncCore"/>, which closes the databases). The database members are
/// re-exposed typed with <c>new</c> members over the base's public members, and
/// <see cref="TryGetDatabase(DatabaseName, out KeyValueDatabase)"/> is a typed overload of the
/// base's lookup (the parameter types differ, so it hides nothing).
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public sealed class KeyValueDatabaseEngine : DatabaseEngine
{
    private readonly KeyValueDatabaseEngineOptions _options;
    private readonly Dictionary<string, KeyValueDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();
    private readonly ManualResetEventSlim _commitPendingSignal = new();
    private readonly Action _signalCommitPending;
    private readonly KeyValueStorageStrategy _strategy;

    // Woken by a storage whose journal reached the checkpoint size, and by a coordinator that
    // deferred an undo, so the checkpoint and version-purge workers act at once (#1254, #1226).
    private readonly ManualResetEventSlim _checkpointNeededSignal = new();
    private readonly ManualResetEventSlim _undoDeferredSignal = new();
    private readonly int _bufferPoolPages;

    private KeyValueStorage[] _storageSnapshot = [];
    private KeyValueDatabase[] _instanceSnapshot = [];

    // The databases the builder declared (owner decision 56): set once, before they are opened.
    private DatabaseName[] _declaredDatabases = [];

    /// <summary>
    /// The storage-name suffix of the dedicated catalog file set each database owns.
    /// </summary>
    internal const string CatalogSuffix = ".catalog";

    /// <summary>
    /// The buffer pool of a catalog file set, in pages (1 MiB): a catalog holds index
    /// registrations and the format marker, a handful of pages, so
    /// <see cref="KeyValueDatabaseEngineOptions.BufferPoolCapacity"/> sizes the data file set alone.
    /// </summary>
    internal const int CatalogBufferPoolPages = 128;

    // How the model's messages start: "Key-value engine '{name}' …".
    internal const string ModelName = "Key-value";

    private KeyValueDatabaseEngine(string name, KeyValueDatabaseEngineOptions options)
        : base(name, EngineModel.KeyValueStore, options.WorkerFailureWindow, options.WorkerFailureMinimumPasses, options.TimeProvider)
    {
        _options = options;
        _signalCommitPending = _commitPendingSignal.Set;
        _bufferPoolPages = Assimalign.Cohesion.Database.Storage.Storage.GetBufferPoolPageCount(options.BufferPoolCapacity, nameof(options.BufferPoolCapacity));
        JournalSizeLimit = DatabaseWorkerLimits.GetJournalSizeLimit(options.JournalSizeLimit, options.CheckpointJournalSize);

        // Resolve the storage strategy at creation: the engine is operational from
        // the moment the constructor returns (create → use → dispose; no start).
        _strategy = options.StorageStrategy
            ?? (options.RootPath is { IsEmpty: false } strategyRoot
                ? new FileSystemKeyValueStorageStrategy(strategyRoot, options.Durability)
                : new InMemoryKeyValueStorageStrategy(options.Durability));

        if (options.RootPath is { IsEmpty: false } root)
        {
            Directory.CreateDirectory(root);
        }

        // Attach the built-in workers last, after every field they observe is initialized: the
        // base starts each one's pump on a dedicated background thread as it attaches it, alive
        // until disposal. Embedded and hosted consumers get identical durability behavior because
        // nothing outside the engine participates (R10).
        AttachWorker(new KeyValueWriteAheadFlushWorker(this, _commitPendingSignal));
        AttachWorker(new KeyValuePageWriteBackWorker(this));
        AttachWorker(new KeyValueCheckpointWorker(this));
        AttachWorker(new KeyValueVersionPurgeWorker(this));
        AttachWorker(new KeyValueIndexMaintenanceWorker(this));
    }

    /// <inheritdoc />
    public override IReadOnlyList<DatabaseName> OfflineDatabases
    {
        get
        {
            List<DatabaseName>? offline = null;
            foreach (var database in GetInstanceSnapshot())
            {
                // A database its holder is closing is not one of the open databases any more.
                if (database.IsOffline && !database.IsClosed)
                {
                    (offline ??= []).Add(database.Name);
                }
            }

            return offline is null ? [] : offline.AsReadOnly();
        }
    }

    /// <summary>
    /// Gets the engine options, for the engine's background workers.
    /// </summary>
    internal KeyValueDatabaseEngineOptions EngineOptions => _options;

    /// <summary>
    /// Gets the journal size limit the checkpointer applies, resolved from
    /// <see cref="KeyValueDatabaseEngineOptions.JournalSizeLimit"/> when the engine was created
    /// (owner decision 25).
    /// </summary>
    internal long JournalSizeLimit { get; }

    /// <summary>
    /// Gets a point-in-time snapshot of every open storage file set (the data and
    /// catalog sets of every open database), for the engine's background workers.
    /// The snapshot is rebuilt when databases open or close; a worker pass may
    /// therefore race a drop, which workers tolerate.
    /// </summary>
    internal KeyValueStorage[] GetStorageSnapshot() => Volatile.Read(ref _storageSnapshot);

    /// <summary>
    /// Gets a point-in-time snapshot of every open database, for workers that operate
    /// through the per-database transaction coordinator (checkpointing, version purge).
    /// Same racing-a-drop tolerance as <see cref="GetStorageSnapshot"/>.
    /// </summary>
    internal KeyValueDatabase[] GetInstanceSnapshot() => Volatile.Read(ref _instanceSnapshot);

    /// <summary>
    /// Gets the signal a storage sets when its journal reaches
    /// <see cref="KeyValueDatabaseEngineOptions.CheckpointJournalSize"/>; the checkpoint worker waits on it.
    /// </summary>
    internal ManualResetEventSlim CheckpointNeededSignal => _checkpointNeededSignal;

    /// <summary>
    /// Gets the signal a database's coordinator sets when it defers an undo; the version-purge
    /// worker waits on it so the first retry runs about 100 ms later, not a maintenance interval.
    /// </summary>
    internal ManualResetEventSlim UndoDeferredSignal => _undoDeferredSignal;

    /// <summary>
    /// Reports whether <paramref name="database"/> is still one of the engine's open databases:
    /// false once it was dropped, closed for a reopen, the engine closed it, or a holder of the
    /// database disposed it (directly or through a session's
    /// <see cref="KeyValueDatabaseSession.Database"/>, the same instance; the engine keeps such a
    /// database registered until its close ends, then forgets it). A worker pass that raced such a
    /// close tolerates the <see cref="ObjectDisposedException"/> it gets; one from a database still
    /// open is a failure.
    /// </summary>
    /// <param name="database">The database a worker pass visited.</param>
    internal bool IsOpen(KeyValueDatabase database) => !database.IsClosed && Array.IndexOf(GetInstanceSnapshot(), database) >= 0;

    /// <summary>
    /// Creates a new key-value database engine of that name from options. The engine is
    /// operational — background workers running — when this method returns, and its
    /// composition is complete: it takes no further worker or server, and it declares no database.
    /// </summary>
    /// <param name="name">The engine name, written once (owner decision 52 of 2026-10-09).</param>
    /// <param name="options">
    /// Engine creation options. The engine keeps a copy, so a later change to
    /// <paramref name="options"/> does not reach it.
    /// </param>
    /// <returns>A new engine instance.</returns>
    /// <remarks>
    /// The path for embedded code and tests that need no declared database and no factory-built
    /// product. <see cref="CreateBuilder(string)"/> composes workers and servers and opens or creates
    /// the databases it declares before its build returns. Every option refusal names the engine and
    /// the option (<c>Key-value engine '{name}': …</c>).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="KeyValueDatabaseEngineOptions.BufferPoolCapacity"/> is not a whole number of 8 KiB
    /// pages of at least 1 MiB; <see cref="KeyValueDatabaseEngineOptions.CheckpointJournalSize"/> is
    /// negative; <see cref="KeyValueDatabaseEngineOptions.CheckpointInterval"/> or
    /// <see cref="KeyValueDatabaseEngineOptions.MaintenanceInterval"/> is not positive;
    /// <see cref="KeyValueDatabaseEngineOptions.GroupCommitWindow"/> is not positive or is longer than
    /// <see cref="Assimalign.Cohesion.Database.Storage.Storage.MaximumGroupCommitWindow"/>;
    /// <see cref="KeyValueDatabaseEngineOptions.WorkerFailureWindow"/> is not positive or is longer
    /// than <see cref="DatabaseEngine.MaximumWorkerFailureWindow"/>;
    /// <see cref="KeyValueDatabaseEngineOptions.WorkerFailureMinimumPasses"/> is less than one; or
    /// <see cref="KeyValueDatabaseEngineOptions.JournalSizeLimit"/> is negative, or set and below
    /// <see cref="KeyValueDatabaseEngineOptions.CheckpointJournalSize"/>.
    /// </exception>
    public static KeyValueDatabaseEngine Create(string name, KeyValueDatabaseEngineOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);

        // The engine keeps a copy (B3 of the engine extensibility design): its write-back worker
        // reads the batch size on every pass, so a caller's later change used to reach it.
        var engine = CreateUncomposed(name, options.Snapshot());
        engine.CompleteComposition();
        return engine;
    }

    /// <summary>
    /// Creates a dependency-free builder for the key-value engine of that name: its options, the
    /// databases it declares, and its nested worker and server factories.
    /// </summary>
    /// <param name="name">The engine name, written once (owner decision 52 of 2026-10-09).</param>
    /// <returns>A fresh builder supporting one engine construction attempt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    public static KeyValueDatabaseEngineBuilder CreateBuilder(string name) => new(name);

    /// <summary>
    /// Creates an operational engine whose composition is still open, for the builder, which
    /// attaches the products of its factories through <see cref="Compose"/>.
    /// </summary>
    /// <param name="name">The engine name.</param>
    /// <param name="options">
    /// Engine creation options, already a copy the caller does not change again
    /// (<see cref="KeyValueDatabaseEngineOptions.Snapshot"/>): the engine keeps this object.
    /// </param>
    /// <returns>A new engine instance.</returns>
    internal static KeyValueDatabaseEngine CreateUncomposed(string name, KeyValueDatabaseEngineOptions options)
    {
        ValidateOptions(name, options);
        return new KeyValueDatabaseEngine(name, options);
    }

    /// <summary>
    /// Checks the name and options an engine is created from, before anything is created: the
    /// checks of <see cref="Create"/>, which the builder also makes first. Each option refusal names
    /// the engine.
    /// </summary>
    /// <param name="name">The engine name.</param>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">See <see cref="Create"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">See <see cref="Create"/>.</exception>
    internal static void ValidateOptions(string name, KeyValueDatabaseEngineOptions options)
    {
        // Checked before the constructor spawns the worker threads; the base refuses a blank name
        // too, after the leaf's fields were created.
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        string engine = DatabaseEngineOptionChecks.Describe(ModelName, name);

        DatabaseEngineOptionChecks.GetBufferPoolPageCount(options.BufferPoolCapacity, engine, nameof(options.BufferPoolCapacity));
        DatabaseEngineOptionChecks.ThrowIfNegative(options.CheckpointJournalSize, engine, nameof(options.CheckpointJournalSize));
        DatabaseEngineOptionChecks.ThrowIfNotPositive(options.CheckpointInterval, engine, nameof(options.CheckpointInterval));
        DatabaseEngineOptionChecks.ThrowIfNotPositive(options.MaintenanceInterval, engine, nameof(options.MaintenanceInterval));
        DatabaseWorkerLimits.Validate(options.WorkerFailureWindow, options.WorkerFailureMinimumPasses, options.JournalSizeLimit, options.CheckpointJournalSize,
            nameof(options.WorkerFailureWindow), nameof(options.WorkerFailureMinimumPasses), nameof(options.JournalSizeLimit), engine);

        // Checked here, before any file is touched, rather than by the storage setter at database
        // create or open (owner decision 26 of 2026-10-06): the window is also the flush worker's
        // wake cadence, so it must be positive, and a monitor wait takes no longer timeout.
        DatabaseEngineOptionChecks.ThrowIfInvalidGroupCommitWindow(options.GroupCommitWindow, engine, nameof(options.GroupCommitWindow));
    }

    /// <summary>
    /// Gets the databases the engine's builder declared, in declaration order; empty for an engine
    /// <see cref="Create"/> made.
    /// </summary>
    internal IReadOnlyList<DatabaseName> DeclaredDatabases => Volatile.Read(ref _declaredDatabases);

    /// <summary>
    /// Records the databases the builder declared, before it opens them: from then on
    /// <see cref="DatabaseEngine.DropDatabaseAsync"/> refuses each of them (owner decision 56 of
    /// 2026-10-09).
    /// </summary>
    /// <param name="declarations">The declared databases, in declaration order.</param>
    /// <exception cref="InvalidOperationException">The engine already declares its databases.</exception>
    internal void Declare(DatabaseName[] declarations) => DatabaseDeclarations.Declare(ref _declaredDatabases, declarations, Name);

    /// <summary>
    /// Attaches the products of the builder's factories, workers first and then servers, and
    /// freezes the engine's composition: the builder's compose method
    /// (<c>DatabaseEngineBuilderState.Complete</c>). The base refuses a product attached twice, a
    /// server that fronts another engine, a worker whose name another worker of the engine has, and
    /// a worker that is not free (another engine owns it, or it was released).
    /// </summary>
    /// <param name="workers">The workers, produced one factory at a time as they are requested.</param>
    /// <param name="servers">The servers, produced one factory at a time as they are requested.</param>
    internal void Compose(IEnumerable<DatabaseEngineWorker> workers, IEnumerable<DatabaseServer> servers)
    {
        foreach (var worker in workers)
        {
            AttachWorker(worker);
        }

        foreach (var server in servers)
        {
            AttachServer(server);
        }

        CompleteComposition();
    }

    /// <summary>
    /// Releases a worker the builder refused, or one its failed composition left unattached: the
    /// engine base's <see cref="DatabaseEngine.ReleaseUnownedWorkerAsync"/>, which the builder's
    /// rollback (<c>DatabaseEngineBuilderState.Complete</c>) cannot reach itself. It does nothing on a
    /// worker an engine owns (concrete-types plan, row 7).
    /// </summary>
    /// <param name="worker">The refused worker.</param>
    /// <returns>A task that completes once the worker's resources are released.</returns>
    internal static ValueTask ReleaseRefusedWorkerAsync(DatabaseEngineWorker worker) => ReleaseUnownedWorkerAsync(worker);

    /// <summary>
    /// Creates a new logical key-value database with the specified name.
    /// </summary>
    /// <param name="name">The name of the database to create.</param>
    /// <param name="cancellationToken">Observed before the database is created.</param>
    /// <returns>The newly created database.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the database was created.</exception>
    /// <exception cref="DatabaseException">A database with the same name already exists.</exception>
    public new async ValueTask<KeyValueDatabase> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
        => (KeyValueDatabase)await base.CreateDatabaseAsync(name, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens an existing logical key-value database by name. A database that went offline, or
    /// that a holder closed outside the engine, is reopened: the returned instance is a new one,
    /// opened again from its files.
    /// </summary>
    /// <param name="name">The name of the database to open.</param>
    /// <param name="cancellationToken">Observed before the database is opened, and while the open waits for a holder's close of it.</param>
    /// <returns>The opened database.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the database was opened.</exception>
    /// <exception cref="DatabaseNotFoundException">The database does not exist.</exception>
    /// <exception cref="DatabaseException">A file set or the entry-space format of the database was refused.</exception>
    /// <remarks>
    /// A database a holder closed is forgotten once its close ends (owner decision 33, #1289), and
    /// this opens it again, with recovery over its files, in memory as on disk. An open that finds
    /// the close still running waits for it to end first. Until that decision the engine returned
    /// the closed instance, which refused every session.
    /// </remarks>
    public new async ValueTask<KeyValueDatabase> OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
        => (KeyValueDatabase)await base.OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Enumerates the key-value databases the engine manages. The engine state and the token are
    /// checked when the call is made, not when the enumeration starts.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>An async sequence of the databases.</returns>
    /// <exception cref="ObjectDisposedException">The engine has been disposed when the call is made.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled when the call is made.</exception>
    public new IAsyncEnumerable<KeyValueDatabase> GetDatabasesAsync(CancellationToken cancellationToken = default)
        => (IAsyncEnumerable<KeyValueDatabase>)base.GetDatabasesAsync(cancellationToken);

    /// <summary>
    /// Attempts to retrieve an open key-value database by name without throwing when it is not open.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <param name="database">When this method returns true, the database.</param>
    /// <returns>True when the database is open in the engine; otherwise false.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <remarks>
    /// An overload of the base's <see cref="DatabaseEngine.TryGetDatabase"/>, not a <c>new</c>
    /// member: the parameter types differ, so nothing is hidden. Overload resolution prefers the
    /// most derived applicable method, so an <c>out var</c> or <c>out _</c> call on the engine binds
    /// here, and an explicitly typed <c>out DatabaseInstance</c> binds the base's. It reads the
    /// base's public member and casts once, so the base's name and disposal checks always run.
    /// </remarks>
    public bool TryGetDatabase(DatabaseName name, [MaybeNullWhen(false)] out KeyValueDatabase database)
    {
        if (base.TryGetDatabase(name, out var found))
        {
            database = (KeyValueDatabase)found;
            return true;
        }

        database = null;
        return false;
    }

    /// <inheritdoc />
    protected override ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            // A create that raced the engine's disposal cannot add a database after
            // DisposeAsyncCore closed them.
            ThrowIfDisposed();
            if (_databases.ContainsKey(name))
            {
                throw new DatabaseException($"A database with name '{name}' already exists.");
            }

            var storage = _strategy.CreateStorage(name);
            KeyValueStorage? catalogStorage = null;

            // Publish the storages to the worker snapshot BEFORE constructing the
            // instance: instance construction itself commits (the primary-index
            // bootstrap, the format marker), and under grouped durability those
            // commits need the flush worker to see the storages or they wait out
            // the whole self-help window.
            try
            {
                ConfigureStorage(storage, name, catalog: false);
                catalogStorage = _strategy.CreateStorage(name + CatalogSuffix);
                ConfigureStorage(catalogStorage, name + CatalogSuffix, catalog: true);
                PublishStorageSnapshotLocked(storage, catalogStorage);
                var database = new KeyValueDatabase(name, this, storage, catalogStorage);
                _databases[name] = database;
                return new ValueTask<DatabaseInstance>(database);
            }
            catch
            {
                try
                {
                    storage.Dispose();
                }
                finally
                {
                    catalogStorage?.Dispose();
                }
                throw;
            }
            finally
            {
                RebuildStorageSnapshotLocked();
            }
        }
    }

    /// <inheritdoc />
    protected override ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            // An open that raced the engine's disposal cannot add a database after
            // DisposeAsyncCore closed them.
            ThrowIfDisposed();
            if (_databases.TryGetValue(name, out var existing))
            {
                if (existing.IsClosed || !existing.IsOffline)
                {
                    // A database its holder is closing is returned as it is, without touching its
                    // files: the base waits for the close, which forgets it, and opens it again.
                    return new ValueTask<DatabaseInstance>(existing);
                }

                // The database went offline after a failed durable flush (#1243): reopening it
                // is the one way back. Its close writes nothing, and the open below runs
                // recovery, which decides the outcome of every commit that was not confirmed.
                // The engine lets it go before it closes it, and the close waits for one a holder
                // started meanwhile, so the open below never races it for the files.
                _databases.Remove(name);
                RebuildStorageSnapshotLocked();
                existing.Dispose();
            }

            if (!_strategy.StorageExists(name))
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            KeyValueStorage storage;
            try
            {
                storage = _strategy.OpenStorage(name);
            }
            catch (StorageFormatException exception)
            {
                throw RefuseStorageFormat(name, "data", name, exception);
            }

            KeyValueStorage? catalogStorage = null;

            // See CreateDatabaseCoreAsync: instance construction commits (recovery
            // checkpoint, primary-index re-attachment), so the flush worker must
            // see the storages first under grouped durability.
            try
            {
                ConfigureStorage(storage, name, catalog: false);
                try
                {
                    catalogStorage = _strategy.StorageExists(name + CatalogSuffix)
                        ? _strategy.OpenStorage(name + CatalogSuffix)
                        : _strategy.CreateStorage(name + CatalogSuffix);
                }
                catch (StorageFormatException exception)
                {
                    throw RefuseStorageFormat(name, "catalog", name + CatalogSuffix, exception);
                }

                ConfigureStorage(catalogStorage, name + CatalogSuffix, catalog: true);
                PublishStorageSnapshotLocked(storage, catalogStorage);
                var database = new KeyValueDatabase(name, this, storage, catalogStorage, recover: true);
                _databases[name] = database;
                return new ValueTask<DatabaseInstance>(database);
            }
            catch
            {
                try
                {
                    storage.Dispose();
                }
                finally
                {
                    catalogStorage?.Dispose();
                }
                throw;
            }
            finally
            {
                RebuildStorageSnapshotLocked();
            }
        }
    }

    /// <summary>
    /// Names the database and the file set behind a storage format refusal; the refusal itself
    /// names the formats found and supported, and the remedy.
    /// </summary>
    /// <param name="name">The database.</param>
    /// <param name="role">Which of the database's file sets was refused: <c>data</c> or <c>catalog</c>.</param>
    /// <param name="storageName">The file set's storage name.</param>
    /// <param name="exception">The storage's refusal.</param>
    private static DatabaseException RefuseStorageFormat(string name, string role, string storageName, StorageFormatException exception)
        => new($"Database '{name}' cannot be opened: its {role} file set '{storageName}' was refused. {exception.Message}", exception);

    /// <inheritdoc />
    /// <exception cref="DatabaseObjectLockedException">
    /// The engine's builder declared the database (owner decision 56 of 2026-10-09): the declaration
    /// owns it, so it leaves only when the declaration does.
    /// </exception>
    protected override ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        if (DatabaseDeclarations.TryFind(DeclaredDatabases, name, out var declared))
        {
            throw DatabaseDeclarations.RefuseDrop(ModelName, Name, declared, nameof(KeyValueDatabaseEngineBuilder));
        }

        lock (_syncRoot)
        {
            ThrowIfDisposed();
            if (_databases.TryGetValue(name, out var database))
            {
                // Publish the shrunken snapshot before disposing so worker passes
                // stop touching the storage as early as possible (a pass already in
                // flight may still race the dispose, which workers tolerate). The
                // close waits for one a holder started, so the files are dropped only
                // once nothing holds them.
                _databases.Remove(name);
                RebuildStorageSnapshotLocked();
                database.Dispose();
            }

            _strategy.DropStorage(name);

            if (_strategy.StorageExists(name + CatalogSuffix))
            {
                _strategy.DropStorage(name + CatalogSuffix);
            }
        }

        return default;
    }

    /// <inheritdoc />
    protected override IAsyncEnumerable<DatabaseInstance> GetDatabasesCore(CancellationToken cancellationToken)
        => EnumerateDatabasesAsync(cancellationToken);

    /// <inheritdoc />
    protected override bool TryGetDatabaseCore(DatabaseName name, [MaybeNullWhen(false)] out DatabaseInstance database)
    {
        lock (_syncRoot)
        {
            if (_databases.TryGetValue(name, out var found))
            {
                database = found;
                return true;
            }
        }

        database = null;
        return false;
    }

    /// <inheritdoc />
    protected override StorageOfflineException? GetOfflineErrorCore(DatabaseName name)
        => FindOpen(name)?.OfflineError;

    /// <inheritdoc />
    /// <remarks>
    /// The data file set goes offline with the cause, and its hook takes the catalog file set
    /// offline and ends the database's lock waits, as after a failed durable flush (#1243).
    /// </remarks>
    protected override bool TakeDatabaseOfflineCore(DatabaseName name, StorageOfflineCause cause, string reason, Exception failure)
        => FindOpen(name) is { IsOffline: false } database && database.DataStorage.TakeOffline(cause, reason, failure);

    /// <inheritdoc />
    protected override void ForgetClosedDatabaseCore(DatabaseInstance database)
        => DatabaseRegistry.Forget((KeyValueDatabase)database, _syncRoot, GetInstanceSnapshot, ForgetLocked);

    // The open database of that name in the published snapshot, or null: lock-free, so a worker
    // that gives up on a database never waits for a create, open or drop holding the lock.
    private KeyValueDatabase? FindOpen(DatabaseName name)
    {
        foreach (var database in GetInstanceSnapshot())
        {
            if (!database.IsClosed && string.Equals(database.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return database;
            }
        }

        return null;
    }

    /// <summary>
    /// Closes every open database once the base disposed the servers, stopped the worker pumps
    /// and disposed the workers: each database durably flushes according to its storage's
    /// durability policy, and an offline one closes without writing. A close a holder started is
    /// waited for. Then the engine's worker signals, and an in-memory engine's files, are released.
    /// </summary>
    /// <returns>A task that completes once every database is closed.</returns>
    protected override async ValueTask DisposeAsyncCore()
    {
        KeyValueDatabase[] snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _databases.Values];
            _databases.Clear();
            _storageSnapshot = [];
            _instanceSnapshot = [];
        }

        List<Exception>? failures = null;
        foreach (var database in snapshot)
        {
            try
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                (failures ??= []).Add(failure);
            }
        }

        _commitPendingSignal.Dispose();
        _checkpointNeededSignal.Dispose();
        _undoDeferredSignal.Dispose();

        // An in-memory engine's files go with it: nothing opens them again (#1272).
        (_strategy as InMemoryKeyValueStorageStrategy)?.Release();

        // The base reports this step's failure among the engine's components: one database's
        // failure as itself, several together.
        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more key-value databases failed to close.", failures);
        }
    }

    /// <summary>
    /// Configures a freshly created or opened storage file set with the engine's
    /// durability policy, its buffer pool capacity and checkpoint size (#1254), and wires
    /// its commit-pending and checkpoint-needed hooks to the engine's worker signals.
    /// </summary>
    /// <remarks>
    /// The engine sets the pool on whatever storage its strategy returns, so a storage
    /// strategy needs no knowledge of the option.
    /// </remarks>
    private void ConfigureStorage(KeyValueStorage storage, string storageName, bool catalog)
    {
        storage.ConfigureCommitDurability(_options.Durability, $"{_strategy.GetType().Name} ({storageName})");
        storage.GroupCommitWindow = _options.GroupCommitWindow;
        storage.OnCommitPending = _signalCommitPending;
        storage.BufferPoolCapacity = catalog ? CatalogBufferPoolPages : _bufferPoolPages;
        storage.CheckpointJournalSize = _options.CheckpointJournalSize;
        storage.OnCheckpointNeeded = _checkpointNeededSignal.Set;
    }

    /// <summary>
    /// Publishes a provisional storage snapshot containing the open databases'
    /// storages plus the given not-yet-registered ones — called before instance
    /// construction so the flush/write-back workers can serve commits the
    /// construction itself performs.
    /// </summary>
    private void PublishStorageSnapshotLocked(KeyValueStorage storage, KeyValueStorage catalogStorage)
    {
        var current = _storageSnapshot;
        var storages = new KeyValueStorage[current.Length + 2];
        current.CopyTo(storages, 0);
        storages[^2] = storage;
        storages[^1] = catalogStorage;
        Volatile.Write(ref _storageSnapshot, storages);
    }

    // Under the engine lock: removes a database whose close ended, when the registry still holds
    // that instance (a reopen may have registered a new one of the same name).
    private void ForgetLocked(KeyValueDatabase database)
    {
        if (_databases.TryGetValue(database.Name, out var tracked) && ReferenceEquals(tracked, database))
        {
            _databases.Remove(database.Name);
            RebuildStorageSnapshotLocked();
        }
    }

    /// <summary>
    /// Rebuilds the storage snapshot the background workers iterate. Called under
    /// the engine lock whenever the open-database set changes.
    /// </summary>
    private void RebuildStorageSnapshotLocked()
    {
        var storages = new KeyValueStorage[_databases.Count * 2];
        var instances = new KeyValueDatabase[_databases.Count];
        int index = 0;
        int instanceIndex = 0;

        foreach (var database in _databases.Values)
        {
            storages[index++] = database.DataStorage;
            storages[index++] = database.CatalogStorage;
            instances[instanceIndex++] = database;
        }

        Volatile.Write(ref _storageSnapshot, storages);
        Volatile.Write(ref _instanceSnapshot, instances);
    }

    // The databases as one typed sequence, which GetDatabasesAsync hands out without a per-item
    // cast: the sequence is covariant, so it is the base's sequence too.
    private async IAsyncEnumerable<KeyValueDatabase> EnumerateDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        KeyValueDatabase[] snapshot;
        lock (_syncRoot)
        {
            snapshot = [.. _databases.Values];
        }

        foreach (var database in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A database its holder is closing is about to be forgotten: not one of the engine's.
            if (!database.IsClosed)
            {
                yield return database;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
