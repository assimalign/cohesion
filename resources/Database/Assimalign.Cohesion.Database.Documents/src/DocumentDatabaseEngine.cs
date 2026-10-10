using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Manages logical document databases and their four maintenance workers.</summary>
/// <remarks>
/// <para>
/// The engine is a data machine: <see cref="Create"/> returns it operational, its four built-in
/// workers (write-ahead-log group-commit flusher, page write-back, checkpointer and version purge)
/// already pumping, and disposal is its one lifecycle transition, which closes every database
/// according to its storage durability. Each database's files live in a directory of its own under
/// <see cref="DocumentDatabaseEngineOptions.RootPath"/>, or in memory when no root is set; the
/// engine keeps an in-memory database's files for its own lifetime, so a closed in-memory database
/// reopens with its data (#1272).
/// </para>
/// <para>
/// <b>A database its holder closed.</b> A database disposed outside the engine (directly, or
/// through a session's <see cref="DocumentDatabaseSession.Database"/>) is forgotten once its close
/// ends (owner decision 33 of 2026-10-06, #1289), so a later
/// <see cref="OpenDatabaseAsync(DatabaseName, CancellationToken)"/> opens it again from its files,
/// in memory as on disk, with its documents. Until the close ends the engine's workers skip it
/// (<see cref="IsOpen(DocumentDatabase)"/> is false for it), so the engine stays
/// <see cref="EngineState.Running"/>.
/// </para>
/// <para>
/// <b>Declared databases</b> (B3 of the engine extensibility design). An engine built through
/// <see cref="CreateBuilder(string)"/> has opened, or created, every database its builder declared
/// (<see cref="DocumentDatabaseEngineBuilder.AddDatabase(string)"/>) before the build returns. The
/// declaration owns each of them, so <see cref="DatabaseEngine.DropDatabaseAsync"/> refuses it with
/// <see cref="DatabaseObjectLockedException"/> (owner decision 56 of 2026-10-09).
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A sealed leaf of the root
/// <see cref="DatabaseEngine"/>: the base owns the name, the model, the worker pumps, the state
/// fold, the composition attach and freeze, the argument and disposed checks of every public
/// member, and the disposal order (servers, the worker pumps and the workers, then
/// <see cref="DisposeAsyncCore"/>, which closes the databases). The database members are
/// re-exposed typed with <c>new</c> members over the base's public members, and
/// <see cref="TryGetDatabase(DatabaseName, out DocumentDatabase)"/> is a typed overload of the base's
/// lookup (the parameter types differ, so it hides nothing). The model's own name check, that a
/// database name is a single file-name component, runs in the cores, after the base's checks.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public sealed class DocumentDatabaseEngine : DatabaseEngine
{
    // How the model's messages start: "Document engine '{name}' …".
    internal const string ModelName = "Document";

    private readonly DocumentDatabaseEngineOptions _options;
    private readonly Dictionary<string, DocumentDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _commitPending = new();

    // Woken by a storage whose journal reached the checkpoint size, and by a coordinator that
    // deferred an undo, so the checkpoint and version-purge workers act at once (#1254, #1226).
    private readonly ManualResetEventSlim _checkpointNeeded = new();
    private readonly ManualResetEventSlim _undoDeferred = new();
    private readonly string? _rootPath;

    // The files of the engine's in-memory databases (no strategy and no root), kept for the
    // engine's lifetime so a closed in-memory database reopens with its data (#1272); null otherwise.
    private readonly DatabaseMemoryFiles? _memory;
    private DocumentDatabase[] _instances = [];

    // The databases the builder declared (owner decision 56): set once, before they are opened.
    private DatabaseName[] _declaredDatabases = [];

    private DocumentDatabaseEngine(string name, DocumentDatabaseEngineOptions options)
        : base(name, EngineModel.Document, options.WorkerFailureWindow, options.WorkerFailureMinimumPasses, options.TimeProvider)
    {
        _options = options;
        JournalSizeLimit = DatabaseWorkerLimits.GetJournalSizeLimit(options.JournalSizeLimit, options.CheckpointJournalSize);
        _rootPath = options.StorageStrategy is null && options.RootPath is { IsEmpty: false } root ? Path.GetFullPath(root) : null;
        _memory = options.StorageStrategy is null && _rootPath is null ? new DatabaseMemoryFiles() : null;
        if (_rootPath is not null)
        {
            Directory.CreateDirectory(_rootPath);
        }

        // Attach the built-in workers last, after every field they observe is initialized: the
        // base starts each one's pump on a dedicated background thread, named for the worker, as
        // it attaches it.
        AttachWorker(new DocumentWriteAheadFlushWorker(this, _commitPending));
        AttachWorker(new DocumentPageWriteBackWorker(this));
        AttachWorker(new DocumentCheckpointWorker(this));
        AttachWorker(new DocumentVersionPurgeWorker(this));
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

    internal DocumentDatabaseEngineOptions EngineOptions => _options;

    /// <summary>
    /// Gets the journal size limit the checkpointer applies, resolved from
    /// <see cref="DocumentDatabaseEngineOptions.JournalSizeLimit"/> when the engine was created
    /// (owner decision 25).
    /// </summary>
    internal long JournalSizeLimit { get; }

    internal DocumentDatabase[] GetInstanceSnapshot() => Volatile.Read(ref _instances);

    /// <summary>
    /// Gets the signal a storage sets when its journal reaches
    /// <see cref="DocumentDatabaseEngineOptions.CheckpointJournalSize"/>; the checkpoint worker waits on it.
    /// </summary>
    internal ManualResetEventSlim CheckpointNeededSignal => _checkpointNeeded;

    /// <summary>
    /// Gets the signal a database's coordinator sets when it defers an undo; the version-purge
    /// worker waits on it so the first retry runs about 100 ms later, not a maintenance interval.
    /// </summary>
    internal ManualResetEventSlim UndoDeferredSignal => _undoDeferred;

    /// <summary>
    /// Reports whether <paramref name="database"/> is still one of the engine's open databases:
    /// false once it was dropped, closed for a reopen, the engine closed it, or a holder of the
    /// database disposed it (a session's <see cref="DocumentDatabaseSession.Database"/> included; the
    /// engine keeps such a database registered until its close ends, then forgets it). A worker
    /// pass that raced such a close tolerates the <see cref="ObjectDisposedException"/> it gets;
    /// one from a database still open is a failure.
    /// </summary>
    /// <param name="database">The database a worker pass visited.</param>
    internal bool IsOpen(DocumentDatabase database) => !database.IsClosed && Array.IndexOf(GetInstanceSnapshot(), database) >= 0;

    /// <summary>
    /// Creates a dependency-free builder for the document engine of that name: its options, the
    /// databases it declares, and its nested worker and server factories.
    /// </summary>
    /// <param name="name">The engine name, written once (owner decision 52 of 2026-10-09).</param>
    /// <returns>A fresh builder supporting one engine construction attempt; creating it starts nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <remarks>Use this entry point inside hosting-aware factories to assign already resolved values before Build.</remarks>
    public static DocumentDatabaseEngineBuilder CreateBuilder(string name) => new(name);

    /// <summary>
    /// Creates an operational engine of that name using memory or files under the configured root.
    /// Its workers are running when this method returns, and its composition is complete: it takes
    /// no further worker, and it declares no database.
    /// </summary>
    /// <param name="name">The engine name, written once (owner decision 52 of 2026-10-09).</param>
    /// <param name="options">
    /// The engine configuration. The engine keeps a copy, so a later change to
    /// <paramref name="options"/> does not reach it.
    /// </param>
    /// <returns>The running engine.</returns>
    /// <remarks>
    /// The path for embedded code and tests that need no declared database and no factory-built
    /// product. <see cref="CreateBuilder(string)"/> composes workers and opens or creates the databases
    /// it declares before its build returns. Every option refusal names the engine and the option
    /// (<c>Document engine '{name}': …</c>).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A worker interval or batch size is not positive, the grouped-commit window is not positive or
    /// is longer than <see cref="Assimalign.Cohesion.Database.Storage.Storage.MaximumGroupCommitWindow"/>,
    /// the buffer pool capacity is not a whole number of 8 KiB pages of at least 1 MiB, the
    /// checkpoint journal size is negative, the worker failure window is not positive or is longer
    /// than <see cref="DatabaseEngine.MaximumWorkerFailureWindow"/>, the worker failure minimum of
    /// passes is less than one, or the journal size limit is negative or set and below the
    /// checkpoint journal size.
    /// </exception>
    public static DocumentDatabaseEngine Create(string name, DocumentDatabaseEngineOptions options)
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
    /// Creates an operational engine whose composition is still open, for the builder, which
    /// attaches the products of its factories through <see cref="Compose"/>.
    /// </summary>
    /// <param name="name">The engine name.</param>
    /// <param name="options">
    /// The engine configuration, already a copy the caller does not change again
    /// (<see cref="DocumentDatabaseEngineOptions.Snapshot"/>): the engine keeps this object.
    /// </param>
    /// <returns>The running engine.</returns>
    internal static DocumentDatabaseEngine CreateUncomposed(string name, DocumentDatabaseEngineOptions options)
    {
        ValidateOptions(name, options);
        return new DocumentDatabaseEngine(name, options);
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
    internal static void ValidateOptions(string name, DocumentDatabaseEngineOptions options)
    {
        // Checked before the constructor spawns the worker threads; the base refuses a blank name
        // too, after the leaf's fields were created.
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        string engine = DatabaseEngineOptionChecks.Describe(ModelName, name);

        DatabaseEngineOptionChecks.ThrowIfNotPositive(options.CheckpointInterval, engine, nameof(options.CheckpointInterval));
        DatabaseEngineOptionChecks.ThrowIfNotPositive(options.PageWriteBackInterval, engine, nameof(options.PageWriteBackInterval));
        DatabaseEngineOptionChecks.ThrowIfNotPositive(options.MaintenanceInterval, engine, nameof(options.MaintenanceInterval));
        DatabaseEngineOptionChecks.ThrowIfInvalidGroupCommitWindow(options.GroupCommitWindow, engine, nameof(options.GroupCommitWindow));
        DatabaseEngineOptionChecks.ThrowIfNotPositive(options.PageWriteBackBatchSize, engine, nameof(options.PageWriteBackBatchSize));
        DatabaseEngineOptionChecks.GetBufferPoolPageCount(options.BufferPoolCapacity, engine, nameof(options.BufferPoolCapacity));
        DatabaseEngineOptionChecks.ThrowIfNegative(options.CheckpointJournalSize, engine, nameof(options.CheckpointJournalSize));
        DatabaseWorkerLimits.Validate(options.WorkerFailureWindow, options.WorkerFailureMinimumPasses, options.JournalSizeLimit, options.CheckpointJournalSize,
            nameof(options.WorkerFailureWindow), nameof(options.WorkerFailureMinimumPasses), nameof(options.JournalSizeLimit), engine);
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
    /// Creates a new logical document database with the specified name.
    /// </summary>
    /// <param name="name">The name of the database to create: a single file-name component.</param>
    /// <param name="cancellationToken">Observed before the database is created.</param>
    /// <returns>The newly created database.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, or not a single file-name component.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the database was created.</exception>
    /// <exception cref="DatabaseException">A database with the same name already exists.</exception>
    public new async ValueTask<DocumentDatabase> CreateDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
        => (DocumentDatabase)await base.CreateDatabaseAsync(name, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens an existing logical document database by name. A database that went offline, or that a
    /// holder closed outside the engine, is reopened: the returned instance is a new one, opened
    /// again from its files.
    /// </summary>
    /// <param name="name">The name of the database to open: a single file-name component.</param>
    /// <param name="cancellationToken">Observed before the database is opened, and while the open waits for a holder's close of it.</param>
    /// <returns>The opened database.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, or not a single file-name component.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the database was opened.</exception>
    /// <exception cref="DatabaseNotFoundException">The database does not exist.</exception>
    /// <exception cref="DatabaseException">The database's file set or index format was refused.</exception>
    /// <remarks>
    /// A database a holder closed is forgotten once its close ends (owner decision 33, #1289), and
    /// this opens it again, with recovery over its files, in memory as on disk. An open that finds
    /// the close still running waits for it to end first. Until that decision the engine refused
    /// the reopen with <see cref="ObjectDisposedException"/> until the database was dropped.
    /// </remarks>
    public new async ValueTask<DocumentDatabase> OpenDatabaseAsync(DatabaseName name, CancellationToken cancellationToken = default)
        => (DocumentDatabase)await base.OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Enumerates the document databases the engine manages, opening each one found in storage. The
    /// engine state and the token are checked when the call is made, not when the enumeration
    /// starts.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>An async sequence of the databases, in ordinal name order, ignoring case.</returns>
    /// <exception cref="ObjectDisposedException">
    /// The engine has been disposed when the call is made, when the enumeration starts, or while it
    /// opens a database.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled when the call is made, or while the enumeration opens a database.</exception>
    /// <exception cref="DatabaseNotFoundException">Raised while the enumeration opens a database another caller dropped meanwhile.</exception>
    /// <exception cref="DatabaseException">Raised while the enumeration opens a database whose file set or index format is refused.</exception>
    public new IAsyncEnumerable<DocumentDatabase> GetDatabasesAsync(CancellationToken cancellationToken = default)
        => (IAsyncEnumerable<DocumentDatabase>)base.GetDatabasesAsync(cancellationToken);

    /// <summary>
    /// Attempts to retrieve an open document database by name without throwing when it is not open.
    /// </summary>
    /// <param name="name">The name of the database.</param>
    /// <param name="database">When this method returns true, the database.</param>
    /// <returns>True when the database is open in the engine; otherwise false.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, or not a single file-name component.</exception>
    /// <exception cref="ObjectDisposedException">The engine has been disposed.</exception>
    /// <remarks>
    /// An overload of the base's <see cref="DatabaseEngine.TryGetDatabase"/>, not a <c>new</c>
    /// member: the parameter types differ, so nothing is hidden. Overload resolution prefers the
    /// most derived applicable method, so an <c>out var</c> or <c>out _</c> call on the engine binds
    /// here, and an explicitly typed <c>out DatabaseInstance</c> binds the base's. It reads the
    /// base's public member and casts once, so the base's name and disposal checks always run.
    /// </remarks>
    public bool TryGetDatabase(DatabaseName name, [MaybeNullWhen(false)] out DocumentDatabase database)
    {
        if (base.TryGetDatabase(name, out var found))
        {
            database = (DocumentDatabase)found;
            return true;
        }

        database = null;
        return false;
    }

    /// <inheritdoc />
    protected override ValueTask<DatabaseInstance> CreateDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
        => GetDatabase(name, create: true);

    /// <inheritdoc />
    protected override ValueTask<DatabaseInstance> OpenDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
        => GetDatabase(name, create: false);

    /// <inheritdoc />
    /// <exception cref="DatabaseObjectLockedException">
    /// The engine's builder declared the database (owner decision 56 of 2026-10-09): the declaration
    /// owns it, so it leaves only when the declaration does.
    /// </exception>
    protected override ValueTask DropDatabaseCoreAsync(DatabaseName name, CancellationToken cancellationToken)
    {
        ValidateName(name);
        if (DatabaseDeclarations.TryFind(DeclaredDatabases, name, out var declared))
        {
            throw DatabaseDeclarations.RefuseDrop(ModelName, Name, declared, nameof(DocumentDatabaseEngineBuilder));
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            var directory = FindDirectory(name);
            bool open = _databases.Remove(name, out var database);
            if (!open && !StorageExists(name, directory))
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            // The engine lets the database go before it closes it, and the close waits for one a
            // holder started, so the files are dropped only once nothing holds them.
            RebuildSnapshot();
            database?.Dispose();
            if (_options.StorageStrategy is { } strategy)
            {
                strategy.DropStorage(name);
            }
            else if (directory is not null)
            {
                Directory.Delete(directory, recursive: true);
            }
            else
            {
                _memory?.Drop(name);
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
        ValidateName(name);
        lock (_sync)
        {
            ThrowIfDisposed();
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
        => FindOpen(name)?.DataStorage.OfflineError;

    /// <inheritdoc />
    /// <remarks>
    /// The database's storage goes offline with the cause, and its hook ends the database's lock
    /// waits, as after a failed durable flush (#1243).
    /// </remarks>
    protected override bool TakeDatabaseOfflineCore(DatabaseName name, StorageOfflineCause cause, string reason, Exception failure)
        => FindOpen(name) is { IsOffline: false } database && database.DataStorage.TakeOffline(cause, reason, failure);

    /// <inheritdoc />
    protected override void ForgetClosedDatabaseCore(DatabaseInstance database)
        => DatabaseRegistry.Forget((DocumentDatabase)database, _sync, GetInstanceSnapshot, ForgetLocked);

    // The open database of that name in the published snapshot, or null: lock-free, so a worker
    // that gives up on a database never waits for a create, open or drop holding the lock.
    private DocumentDatabase? FindOpen(DatabaseName name)
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
        DocumentDatabase[] snapshot;
        lock (_sync)
        {
            snapshot = [.. _databases.Values];
            _databases.Clear();
            RebuildSnapshot();
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

        _commitPending.Dispose();
        _checkpointNeeded.Dispose();
        _undoDeferred.Dispose();

        // An in-memory engine's files go with it: nothing opens them again (#1272).
        _memory?.Clear();

        // The base reports this step's failure among the engine's components: one database's
        // failure as itself, several together.
        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more document databases failed to close.", failures);
        }
    }

    private ValueTask<DatabaseInstance> GetDatabase(DatabaseName name, bool create)
    {
        ValidateName(name);
        lock (_sync)
        {
            // Under the engine's lock: a create or open that passed the base's check while
            // disposal began must not add a database after the disposal closed them all.
            ThrowIfDisposed();
            if (_databases.TryGetValue(name, out var existing))
            {
                if (create)
                {
                    throw new DatabaseException($"Database '{name}' already exists.");
                }

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
                RebuildSnapshot();
                existing.Dispose();
            }
            var directory = FindDirectory(name);
            bool exists = StorageExists(name, directory);
            if (create && exists)
            {
                throw new DatabaseException($"Database '{name}' already exists.");
            }

            if (!create && !exists)
            {
                throw new DatabaseNotFoundException($"Database '{name}' does not exist.");
            }

            if (_rootPath is not null && create)
            {
                directory = Path.Combine(_rootPath, name);
                Directory.CreateDirectory(directory);
            }
            DocumentStorage? storage = null;
            try
            {
                storage = _options.StorageStrategy is { } strategy
                    ? create ? strategy.CreateStorage(name, _options.Durability) : strategy.OpenStorage(name, _options.Durability)
                    : OpenStorage(directory, _memory, name, create, _options.Durability);
                if (storage is null)
                {
                    throw new InvalidOperationException("The storage strategy returned null.");
                }
                storage.GroupCommitWindow = _options.GroupCommitWindow;
                storage.OnCommitPending = _commitPending.Set;

                // The engine sizes the pool and arms the checkpoint trigger on whatever storage
                // the strategy returned, so a custom strategy needs no knowledge of them (#1254).
                storage.BufferPoolCapacity = Assimalign.Cohesion.Database.Storage.Storage.GetBufferPoolPageCount(_options.BufferPoolCapacity, nameof(_options.BufferPoolCapacity));
                storage.CheckpointJournalSize = _options.CheckpointJournalSize;
                storage.OnCheckpointNeeded = _checkpointNeeded.Set;
                var database = new DocumentDatabase(name, this, storage, recover: !create);
                _databases.Add(name, database);
                return new ValueTask<DatabaseInstance>(database);
            }
            catch (StorageFormatException exception)
            {
                // The storage refused the file set before recovery wrote anything; the refusal
                // names the formats and the remedy, and this names the database.
                storage?.Dispose();
                throw new DatabaseException($"Database '{name}' cannot be opened. {exception.Message}", exception);
            }
            catch
            {
                storage?.Dispose();
                throw;
            }
            finally { RebuildSnapshot(); }
        }
    }

    // A database's files: in its directory, or, for an in-memory engine, the memory the engine
    // keeps for it, created empty or opened again with the bytes its last storage left.
    private static DocumentStorage OpenStorage(string? directory, DatabaseMemoryFiles? memory, string name, bool create, StorageCommitDurability? durability)
    {
        StorageStream? data = null, journal = null, backup = null;
        try
        {
            if (directory is null)
            {
                bool found = memory is not null && (create
                    ? memory.TryCreate(name, out data, out journal, out backup)
                    : memory.TryOpen(name, out data, out journal, out backup));
                if (!found)
                {
                    throw create
                        ? new DatabaseException($"Database '{name}' already exists.")
                        : new DatabaseNotFoundException($"Database '{name}' does not exist.");
                }
            }
            else
            {
                var mode = create ? FileMode.CreateNew : FileMode.Open;
                data = StorageStream.FromFile(Path.Combine(directory, "document.dat"), mode, FileShare.Read);
                journal = StorageStream.FromFile(Path.Combine(directory, "document.log"), mode, FileShare.Read);
                backup = StorageStream.FromFile(Path.Combine(directory, "document.bak"), mode, FileShare.Read);
            }

            return create ? DocumentStorage.Create(data!, journal!, backup!, name, durability) : DocumentStorage.Open(data!, journal!, backup!, checkpointOnOpen: false, durability);
        }
        catch { data?.Dispose(); journal?.Dispose(); backup?.Dispose(); throw; }
    }

    // Whether a database's storage exists: the strategy's answer, its directory under the root,
    // or the engine's in-memory files.
    private bool StorageExists(string name, string? directory)
        => _options.StorageStrategy?.StorageExists(name) ?? (directory is not null || (_memory?.Exists(name) ?? false));

    // The databases as one typed sequence, which GetDatabasesAsync hands out without a per-item
    // cast: the sequence is covariant, so it is the base's sequence too. Each name found in
    // storage is opened through the engine's public member, so its checks run per database.
    private async IAsyncEnumerable<DocumentDatabase> EnumerateDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string[] names;
        lock (_sync)
        {
            ThrowIfDisposed();
            names = _databases.Keys.Concat(_options.StorageStrategy is { } strategy ? strategy.GetDatabaseNames().Select(name => name.ToString()) : _rootPath is null ? _memory?.Names ?? [] : Directory.EnumerateDirectories(_rootPath)
                .Where(path => File.Exists(Path.Combine(path, "document.dat"))).Select(path => Path.GetFileName(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        foreach (string name in names)
        {
            yield return await OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    private string? FindDirectory(string name) => _rootPath is null ? null : Directory.EnumerateDirectories(_rootPath)
        .FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));

    // The model's own name rule, in the cores, after the base's checks of an empty name, disposal
    // and the token: a database's files live in a directory named for it. The rule is shared with
    // every model (DatabaseFileNames), and the builder checks it when a database is declared.
    private static void ValidateName(string name) => DatabaseFileNames.ThrowIfNotSingleComponent(name);

    // Under the engine lock: removes a database whose close ended, when the registry still holds
    // that instance (a reopen may have registered a new one of the same name).
    private void ForgetLocked(DocumentDatabase database)
    {
        if (_databases.TryGetValue(database.Name, out var tracked) && ReferenceEquals(tracked, database))
        {
            _databases.Remove(database.Name);
            RebuildSnapshot();
        }
    }

    private void RebuildSnapshot()
    {
        Volatile.Write(ref _instances, [.. _databases.Values]);
    }
}
