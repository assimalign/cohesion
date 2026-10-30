using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions.Internal;

/// <summary>
/// The per-database MVCC composition (area DESIGN §3.8): one
/// <see cref="TransactionManager"/> + <see cref="LockManager"/> +
/// <see cref="RecordSpaceVersionStore"/> over the database's data storage,
/// with the manager's transaction log bound to the storage's write-ahead
/// journal. The coordinator owns the sequence-space unification (the manager
/// allocates from the storage's counter, so the journal carries one sequence
/// namespace), the per-statement physical brackets and their apply gate, the
/// pairing seam (<see cref="TryGetStorageTransaction"/> resolves a context's
/// current statement bracket; each engine wraps it in the index manager's
/// <c>TransactionSource</c> delegate), and the checkpoint interlock that keeps
/// truncation classification-safe while logical transactions are in flight.
/// </summary>
/// <remarks>
/// <para>
/// Scope decision: one coordinator (and therefore one manager, lock manager,
/// and version store) <b>per database</b>, not per engine — the journal-bound
/// transaction log, recovery analysis, and the prune bound are all properties
/// of one database's journal and record space, and a per-engine manager would
/// couple unrelated databases' snapshot horizons.
/// </para>
/// <para>
/// <b>The bracket model (per-statement, §3.8's migration path):</b> the
/// physical WAL bracket is per <em>statement</em>, not per transaction — a
/// statement's page mutations open a bracket under the apply gate, commit it
/// (non-durably; the transaction's own commit record owns durability through
/// journal ordering) and release its page locks at statement end. Page-grain
/// contention therefore never outlives a statement, and the apply gate — one
/// writer statement applies at a time per database — removes it entirely as a
/// user-visible conflict surface: model locks (acquired <em>before</em> the gate,
/// never inside it) are the only conflict arbiter. The cost accepted: physical
/// write application is serialized per database; the alternative (concurrent
/// appliers with page-conflict retry loops) reintroduced unbounded retry and
/// gate-invisible deadlocks between page and row waits. Transaction rollback is
/// consequently <em>logical</em>: the version store's ledger undoes the
/// writer's stamps (statement-level failures still revert physically via the
/// statement bracket, and crash recovery scrubs unproven writers from the
/// record space at open).
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("TransactionCoordinator")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Transactions.TransactionCoordinator.DebugView))]
public sealed class TransactionCoordinator : IAsyncDisposable
{
    private readonly Storage _storage;
    private readonly StorageJournal _journal;
    private readonly TransactionRecordSpace _records;
    private readonly TransactionManager _manager;
    private readonly LockManager _lockManager;
    private readonly RecordSpaceVersionStore _versionStore;
    private readonly GatedJournalLog _log;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly Func<TransactionSequence, CancellationToken, ValueTask<long>>? _indexPruner;

    // True in the async flow of a statement apply or undo control holding the apply gate, which is not
    // reentrant: a checkpoint asked for from inside the apply is refused instead of waiting for
    // the gate its own caller holds.
    private readonly AsyncLocal<bool> _insideApply = new();

    // 1 when TryCheckpoint found the apply gate held: the statement that releases the gate next
    // runs the checkpoint first. A failure of that checkpoint is kept for the next TryCheckpoint
    // to throw, because the statement that ran it must not fail for it.
    private int _checkpointDeferred;
    private ExceptionDispatchInfo? _deferredCheckpointFailure;
    private readonly Dictionary<ulong, StorageTransaction> _statementBrackets = new();
    private readonly Dictionary<ulong, TransactionContext> _openContexts = new();
    private readonly object _sync = new();
    private TransactionSequence _recoveredSequenceFloor;

    /// <summary>
    /// Creates the MVCC composition for one database's storage and stamped record space.
    /// </summary>
    /// <param name="storage">The storage owning physical brackets and the sequence allocator.</param>
    /// <param name="journal">The same storage's write-ahead journal.</param>
    /// <param name="records">The engine's record access and location encoding.</param>
    /// <remarks>
    /// The caller retains ownership of storage and journal. On reopen, attach model
    /// indexes, call <see cref="AnalyzeAndScrub"/>, scrub those indexes with its plan,
    /// and call <see cref="CompleteRecovery"/> before admitting any sessions.
    /// </remarks>
    public TransactionCoordinator(Storage storage, StorageJournal journal, TransactionRecordSpace records)
        : this(storage, journal, records, TimeProvider.System)
    {
    }

    /// <summary>Creates the composition with index maintenance run under its apply gate.</summary>
    /// <param name="storage">The storage owning physical brackets and sequence allocation.</param>
    /// <param name="journal">The same storage's journal.</param>
    /// <param name="records">The model's stamped record access.</param>
    /// <param name="indexPruner">Prunes a bounded index batch below the supplied safe horizon.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public TransactionCoordinator(Storage storage, StorageJournal journal, TransactionRecordSpace records,
        Func<TransactionSequence, CancellationToken, ValueTask<long>> indexPruner)
        : this(storage, journal, records, TimeProvider.System, indexPruner ?? throw new ArgumentNullException(nameof(indexPruner)))
    {
    }

    /// <summary>
    /// Creates the composition with the clock the deferred-undo retry schedule reads (tests).
    /// </summary>
    internal TransactionCoordinator(Storage storage, StorageJournal journal, TransactionRecordSpace records, TimeProvider time)
        : this(storage, journal, records, time, null)
    {
    }

    private TransactionCoordinator(Storage storage, StorageJournal journal, TransactionRecordSpace records, TimeProvider time,
        Func<TransactionSequence, CancellationToken, ValueTask<long>>? indexPruner)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(records);
        _storage = storage;
        _journal = journal;
        _records = records;
        _indexPruner = indexPruner;

        // Fully qualified: the coordinator's LockManager property shadows the
        // type name inside this scope.
        var locks = new Transactions.LockManager();
        _versionStore = new RecordSpaceVersionStore(storage, records, _applyGate);
        _log = new GatedJournalLog(this);

        // The manager over the gated journal log: the statement apply admits a bracket
        // only for a context the manager still holds open, and the version-purge pass
        // drives its deferred-undo retry. It releases a transaction's locks through the
        // lock manager's unfiltered release; engine code shares the same lock manager in
        // its engine mode, whose release-all leaves a tracked transaction to the manager
        // and which refuses a request that would wait inside a statement apply (see
        // LockManager). Both learn the storage's name here, for their diagnostics only.
        string database = storage.Name;
        _manager = new TransactionManager(
            _log,
            locks,
            _versionStore,
            ReserveSequence,
            time,
            database);
        locks.EnterEngineMode(_manager.IsTracked, () => _insideApply.Value, database);
        _lockManager = locks;
    }

    /// <summary>
    /// Test hook: invoked under the lifecycle append gate just before the storage
    /// checkpoint, with the writers the checkpoint lists; it may block or throw.
    /// </summary>
    /// <remarks>
    /// Null unless a test of this assembly sets it (<c>InternalsVisibleTo</c>). It stands where
    /// a Transactions.Tests storage double used to intercept the checkpoint through the
    /// removed <c>IStorage</c> contract (#1257): <see cref="Storage.Checkpoint(ReadOnlySpan{long})"/>
    /// is non-virtual, so the call that makes it carries the hook (<c>database-area.md</c>,
    /// "Test doubles"). A throw fails the checkpoint as a storage failure there would.
    /// </remarks>
    internal Action<long[]>? BeforeCheckpoint { get; set; }

    /// <summary>
    /// Test hook: invoked after the manager's sequence allocator reserved a sequence from the
    /// storage, under the manager's begin lock and before the begin record's append.
    /// </summary>
    /// <remarks>
    /// Null unless a test of this assembly sets it. It replaces the interception of the
    /// non-virtual <see cref="Storage.ReserveTransactionSequence"/> through <c>IStorage</c> (#1257).
    /// </remarks>
    internal Action? SequenceReserved { get; set; }

    /// <summary>
    /// Test hook: awaited after manager registration, before coordinator tracking, with neither
    /// lock held. Null unless this assembly's tests pause a begin across coordinator disposal.
    /// </summary>
    internal Func<TransactionContext, ValueTask>? BeforeContextTrackedAsync { get; set; }

    /// <summary>
    /// Test hook: invoked with a transaction's sequence just before its abort record is
    /// appended; a throw rejects the record, as a failed journal append would.
    /// </summary>
    /// <remarks>
    /// Null unless a test of this assembly sets it. It replaces a journal double that decorated
    /// the removed <c>IStorageJournal</c> contract (#1257). Since #1252 a real append fails only on
    /// an offline journal, which also refuses everything after it, so this is the only way to
    /// exercise a lost abort record on a storage that stays online.
    /// </remarks>
    internal Action<long>? BeforeAbortRecord { get; set; }

    // The manager's sequence allocator: the storage's counter, so the journal carries one
    // sequence namespace.
    private TransactionSequence ReserveSequence()
    {
        var sequence = new TransactionSequence((ulong)_storage.ReserveTransactionSequence());
        SequenceReserved?.Invoke();
        return sequence;
    }

    /// <summary>
    /// Gets or sets the longest delay between two retries of a deferred undo; engines set it to
    /// their maintenance interval (60 seconds unless set).
    /// </summary>
    /// <remarks>
    /// A rolled-back writer whose undo failed keeps its locks until a retry completes the undo
    /// (#1226), so the retry runs on its own backoff: due about 100 ms after the deferral, then
    /// doubling after each failed retry up to this limit, and starting over with every new
    /// deferral. Before the owner decision of 2026-10-04 each retry waited a whole maintenance
    /// interval, which held every conflicting writer for 60 seconds per failed attempt.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan DeferredUndoRetryLimit
    {
        get => _manager.DeferredUndoRetryLimit;
        set => _manager.DeferredUndoRetryLimit = value;
    }

    /// <summary>
    /// Gets or sets the delay before the first retry of a deferred undo: 100 ms unless set, and
    /// never more than <see cref="DeferredUndoRetryLimit"/>. Each failed retry doubles it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan DeferredUndoRetryDelay
    {
        get => _manager.DeferredUndoRetryDelay;
        set => _manager.DeferredUndoRetryDelay = value;
    }

    /// <summary>
    /// Gets or sets the hook invoked when a rollback's undo is deferred, so the engine's
    /// version-purge worker wakes for the first retry. Invoked outside the coordinator's locks;
    /// it should only signal.
    /// </summary>
    public Action? OnUndoDeferred
    {
        get => _manager.UndoDeferred;
        set => _manager.UndoDeferred = value;
    }

    /// <summary>
    /// Gets the time until the next retry of a deferred undo is due: zero when it is due now,
    /// null when no undo is deferred. The version-purge worker sleeps no longer than this.
    /// </summary>
    public TimeSpan? NextDeferredUndoRetry => _manager.DeferredUndoRetryDueIn;

    /// <summary>
    /// Retries every deferred undo when the retry backoff says one is due
    /// (<see cref="NextDeferredUndoRetry"/>), and does nothing otherwise. Each writer whose undo
    /// now completes gets its abort record, leaves the active table and releases its locks.
    /// </summary>
    /// <param name="cancellationToken">Observed between writers; a started undo runs to completion.</param>
    /// <returns>The number of versions and index entries the completed undos changed.</returns>
    /// <exception cref="ObjectDisposedException">The coordinator was disposed.</exception>
    /// <remarks>
    /// A retry that fails again doubles the delay and rethrows the first failure, after every
    /// deferred writer was attempted; the writers still deferred wait for the next retry.
    /// </remarks>
    public long RetryDeferredUndo(CancellationToken cancellationToken)
        => _manager.RetryDeferredUndoIfDueAsync(cancellationToken).AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Gets whether the storage this coordinator composes went offline after a failed durable
    /// flush (#1243): nothing more may be written to it, and only a reopen brings it back.
    /// </summary>
    public bool IsStorageOffline => _storage.IsOffline;

    /// <summary>
    /// Ends every wait for a lock of this database because its storage went offline, and fails
    /// every wait that would start from then on: each fails with
    /// <see cref="TransactionAbortedException"/> whose inner exception is
    /// <paramref name="cause"/>, which an engine translates into its coded offline refusal. A
    /// request the lock table can grant at once is still granted; the storage refuses the work.
    /// </summary>
    /// <param name="cause">The error that took the storage offline.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cause"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// An offline database undoes nothing: a transaction that was writing when the storage went
    /// offline keeps its locks until the reopen, whose recovery aborts it, because releasing them
    /// without the undo would let the next holder build on versions that were never undone (the
    /// deferred-undo rule of #1226). A request queued behind such a transaction would therefore
    /// wait for a release that never comes, and only the reopen ended it (#1268 review). An engine
    /// calls this from its storage's <c>Storage.OnOffline</c> hook. Idempotent: the first cause is
    /// kept. The waits are failed asynchronously, so a hook that runs under storage locks does no
    /// lock-table work there.
    /// </para>
    /// <para>
    /// Neo4j's lock client ends the waits of a terminated transaction the same way, by failing
    /// them instead of letting them wait for a grant
    /// (<c>community/lock/src/main/java/org/neo4j/kernel/impl/locking/forseti/ForsetiClient.java:1081-1085</c>).
    /// </para>
    /// </remarks>
    public void AbandonLockWaits(StorageOfflineException cause) => _lockManager.Abandon(cause);

    /// <summary>
    /// Gets the transaction manager sessions begin their contexts on.
    /// </summary>
    public TransactionManager Manager => _manager;

    /// <summary>
    /// Gets the lock manager arbitrating the engine's write conflicts.
    /// </summary>
    /// <remarks>
    /// The manager owns the release of every transaction it manages: it releases a
    /// transaction's locks, as a set, at the moment the transaction leaves its active
    /// table. <see cref="LockManager.ReleaseAll"/> called through this property for a
    /// transaction the manager still tracks therefore releases nothing; the manager's own
    /// release, which follows, covers every grant the transaction holds by then, including
    /// one an operation of the transaction obtained after it ended. That is what keeps a
    /// rolled-back writer whose undo is deferred holding its locks (#1226): an engine's
    /// clean-up of a late grant cannot hand the next writer a lock over versions the undo
    /// has not removed yet. For a transaction the manager no longer tracks, the call
    /// releases as usual. The engine mode does not change which requests fail: when a
    /// transaction ends, the manager fails the requests it still has queued (#1225), at its
    /// release or, for a rollback whose undo is deferred, at the end itself, while the grants
    /// stay. It is the same lock manager the transaction manager holds, in the engine mode this
    /// coordinator installs (<see cref="Transactions.LockManager"/>, plan §6.2); until #1258 it
    /// was a private decorator over it.
    /// </remarks>
    public LockManager LockManager => _lockManager;

    /// <summary>
    /// Gets the version store: the ledger over the record space that makes
    /// logical undo and version pruning executable.
    /// </summary>
    public RecordSpaceVersionStore VersionStore => _versionStore;

    /// <summary>
    /// Gets the number of statement brackets currently applying (test
    /// observability; zero whenever no statement is mid-apply).
    /// </summary>
    public int PairedTransactionCount
    {
        get
        {
            lock (_sync)
            {
                return _statementBrackets.Count;
            }
        }
    }

    /// <summary>
    /// Gets the currently open transaction contexts (for the maintenance
    /// workers' safe prune bound).
    /// </summary>
    public IReadOnlyList<TransactionContext> GetOpenContexts()
    {
        lock (_sync)
        {
            return [.. _openContexts.Values];
        }
    }

    /// <summary>
    /// Resolves the context's current statement bracket for model index and catalog mutations.
    /// </summary>
    /// <param name="context">The logical transaction whose statement is applying.</param>
    /// <param name="transaction">The current bracket, or null when no statement is applying.</param>
    /// <returns>True when the context has a registered statement bracket.</returns>
    /// <remarks>
    /// An engine adapts this method to its storage-transaction pairing contract and
    /// supplies its own exception vocabulary when a bracket is missing.
    /// </remarks>
    public bool TryGetStorageTransaction(TransactionContext context, [NotNullWhen(true)] out StorageTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_sync)
        {
            return _statementBrackets.TryGetValue(context.Sequence.Value, out transaction);
        }
    }

    /// <summary>
    /// Begins a logical transaction: the manager assigns the sequence from the
    /// storage's counter (one namespace) without appending a journal record.
    /// The first admitted statement apply enrolls it as a writer, appending its
    /// logical begin record before opening the physical bracket. Physical brackets
    /// are per statement — see the class remarks.
    /// </summary>
    /// <param name="isolationLevel">The isolation level the transaction runs under.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The transaction context.</returns>
    public ValueTask<TransactionContext> BeginAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default) =>
        BeginAsync(isolationLevel, isSnapshotPin: false, cancellationToken);

    /// <summary>Replaces an unused transaction's isolation context, preserving its identity and begin snapshot.</summary>
    /// <param name="context">The current context, with no statement applies running.</param>
    /// <param name="isolationLevel">The new isolation level.</param>
    /// <returns>The replacement context. Earlier handles share its lifecycle.</returns>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The level is undefined.</exception>
    /// <exception cref="ArgumentException">The context is a statement view or snapshot pin, or a change from READ COMMITTED to fixed visibility has no active begin-time snapshot pin.</exception>
    /// <exception cref="TransactionAbortedException">The context is stale, untracked, ending or has a running statement apply.</exception>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    /// <remarks>
    /// The caller excludes data statements and transaction ends, and retains a begin-time snapshot
    /// pin while READ COMMITTED remains configurable. The manager enforces that a pin registered
    /// before the transaction still protects its begin floor when selecting fixed visibility. It protects historical versions if the
    /// caller subsequently selects Snapshot. Release it after configuration or at transaction end.
    /// No engine locks are acquired. Manager registration precedes coordinator tracking, with no
    /// nested locks; the manager protects the new horizon before the coordinator publishes it.
    /// </remarks>
    public TransactionContext ReplaceIsolationLevel(TransactionContext context, IsolationLevel isolationLevel)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (isolationLevel is not (IsolationLevel.Snapshot or IsolationLevel.ReadCommitted or IsolationLevel.Serializable))
        { throw new ArgumentOutOfRangeException(nameof(isolationLevel)); }
        var replacement = _manager.ReplaceIsolationLevel(context, isolationLevel);
        lock (_sync)
        {
            // Disposal may already have untracked the shared lifecycle. Never re-enroll it.
            if (_openContexts.ContainsKey(context.Sequence.Value))
            { _openContexts[context.Sequence.Value] = replacement; }
        }
        if (context.IsolationLevel != isolationLevel)
        { TransactionEventSource.Log.TransactionIsolationChanged(_storage, replacement); }
        return replacement;
    }

    /// <summary>
    /// Begins a snapshot pin that holds the version-pruning horizon for one statement.
    /// It remains registered for pruning, but contributes no current-transactions gauge or transaction
    /// begin, commit or rollback rate and writes no Verbose transaction lifecycle events.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The pin context, to release through <see cref="RollbackAsync"/> when the statement ends.</returns>
    /// <remarks>
    /// Begin the pin before capturing the statement snapshot. The pin uses the same manager
    /// registration and end paths as a snapshot transaction, including disposal and cancellation.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The transaction manager or storage was disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public ValueTask<TransactionContext> BeginSnapshotPinAsync(CancellationToken cancellationToken = default) =>
        BeginAsync(IsolationLevel.Snapshot, isSnapshotPin: true, cancellationToken);

    private async ValueTask<TransactionContext> BeginAsync(
        IsolationLevel isolationLevel, bool isSnapshotPin, CancellationToken cancellationToken)
    {
        // Manager registration (including the immutable pin kind) finishes under its lock
        // before this coordinator takes _sync. Neither lock is held while acquiring the
        // other or while waiting on apply/checkpoint/log gates. Disposal therefore sees
        // the kind before it can end a pin, with the same horizon protection as any snapshot.
        var context = isSnapshotPin
            ? await _manager.BeginSnapshotPinAsync(cancellationToken).ConfigureAwait(false)
            : await _manager.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

        if (BeforeContextTrackedAsync is { } beforeTracking)
        {
            await beforeTracking(context).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _openContexts[context.Sequence.Value] = context;
            TransactionEventSource.Log.TransactionTracked(context);
        }

        // Only the manager's disposal can have ended the context by now (it aborts every active
        // one), and that abort completes before this coordinator's disposal clears the table. If
        // the clearing already ran, the context was added after it and nothing would untrack it,
        // so it leaves the current-transactions gauge here (UntrackEnded does nothing for an
        // active context); if the clearing runs later, it counts the context and UntrackEnded's
        // Remove finds nothing.
        UntrackEnded(context);

        TransactionEventSource.Log.TransactionBegun(_storage, context);
        return context;
    }

    /// <summary>
    /// Commits the transaction through the manager. For an enrolled writer, the
    /// journal-bound log appends the commit record and awaits durability (which,
    /// by journal ordering, also makes every statement bracket's records durable)
    /// while the transaction is still in the manager's active table; the version
    /// store then retains the writer's tombstones for pruning. A reader ends and
    /// releases its locks without appending a lifecycle record or awaiting durability.
    /// </summary>
    /// <param name="context">The transaction to commit.</param>
    /// <param name="cancellationToken">
    /// Observed only before the manager claims the end. A token canceled by then leaves
    /// the transaction active; a started commit completes without observing the token.
    /// </param>
    /// <returns>A task that completes when the transaction's commit ends.</returns>
    /// <exception cref="OperationCanceledException">The token was canceled before the commit started.</exception>
    /// <exception cref="TransactionAbortedException">The transaction was aborted instead of committed.</exception>
    /// <exception cref="TransactionCommitUnconfirmedException">
    /// The transaction committed, but its commit record could not be made durable (see
    /// <see cref="TransactionManager.CommitAsync"/>). Its tombstones are retained for
    /// pruning like those of any committed writer.
    /// </exception>
    public async ValueTask CommitAsync(TransactionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _manager.CommitAsync(context, cancellationToken).ConfigureAwait(false);
            _versionStore.OnCommitted(context.Sequence);
            TransactionEventSource.Log.TransactionCommitted(_storage, context);
        }
        catch (TransactionCommitUnconfirmedException exception)
        {
            _versionStore.OnCommitted(context.Sequence);
            TransactionEventSource.Log.CommitUnconfirmed(_storage, context, exception);
            throw;
        }
        finally
        {
            UntrackEnded(context);
        }
    }

    /// <summary>
    /// Rolls the transaction back through the manager: the version store's
    /// ledger physically undoes the writer's stamps (created versions deleted,
    /// tombstones cleared), and locks release. Only enrolled writers append an
    /// abort record, without awaiting its durability. A reader ends and releases
    /// its locks without appending a lifecycle record or awaiting durability.
    /// </summary>
    /// <param name="context">The transaction to roll back.</param>
    /// <param name="cancellationToken">
    /// Observed only before the rollback starts. A token canceled by then throws
    /// <see cref="OperationCanceledException"/> and leaves the transaction active;
    /// a started rollback runs to completion.
    /// </param>
    /// <remarks>
    /// A started rollback always ends the transaction (#1226), even when the abort
    /// record cannot be written. When the undo itself fails, the transaction still
    /// ends, but the writer keeps its locks and stays in flight for every snapshot
    /// until <see cref="RunVersionPurgePass"/> completes the undo; the project
    /// DESIGN.md, "Ending a transaction", records the rule.
    /// </remarks>
    public async ValueTask RollbackAsync(TransactionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _manager.RollbackAsync(context, cancellationToken).ConfigureAwait(false);
            TransactionEventSource.Log.TransactionRolledBack(_storage, context);
        }
        finally
        {
            UntrackEnded(context);
        }
    }

    // These anonymous controls remain internal until named/session scopes are integrated.
    internal ValueTask<TransactionUndoMarker> CreateUndoMarkerAsync(TransactionContext context, CancellationToken cancellationToken = default)
        => RunUndoControlAsync(context, null, rollback: false, release: false, cancellationToken);

    internal async ValueTask ReleaseUndoMarkerAsync(TransactionContext context, TransactionUndoMarker marker, CancellationToken cancellationToken = default)
        => _ = await RunUndoControlAsync(context, marker, rollback: false, release: true, cancellationToken).ConfigureAwait(false);

    internal async ValueTask RollbackToUndoMarkerAsync(TransactionContext context, TransactionUndoMarker marker, CancellationToken cancellationToken = default)
        => _ = await RunUndoControlAsync(context, marker, rollback: true, release: false, cancellationToken).ConfigureAwait(false);

    // Deterministic admission seams for the owning test assembly.
    internal Action? UndoMarkerValidated { get; set; }
    internal Action? UndoControlAdmitted { get; set; }

    private async ValueTask<TransactionUndoMarker> RunUndoControlAsync(TransactionContext context, TransactionUndoMarker? marker,
        bool rollback, bool release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (rollback || release) { ArgumentNullException.ThrowIfNull(marker); }
        ThrowIfInsideApply();
        if (!rollback)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = _manager.EnterUndoControl(context, out _);
            try
            {
                UndoControlAdmitted?.Invoke();
                if (release)
                {
                    bool anyMarkers = _versionStore.ReleaseMarker(context.Sequence, marker!);
                    _lockManager.ReleaseUndoMarkers(context.Sequence, anyMarkers);
                    _records.UndoScopesChanged(context.Sequence, anyMarkers, _versionStore);
                }
                else
                {
                    marker = _versionStore.CreateMarker(context.Sequence);
                    _lockManager.CreateUndoMarker(context.Sequence, marker);
                    context.MarkUndoScopes();
                    try { _records.UndoScopesChanged(context.Sequence, true, _versionStore); }
                    catch
                    {
                        // A failed registration must not strand a marker whose token was
                        // never returned. Model hooks are exception-atomic on creation;
                        // release/end notifications are nonthrowing lifecycle cleanup.
                        bool anyMarkers = _versionStore.ReleaseMarker(context.Sequence, marker);
                        _lockManager.ReleaseUndoMarkers(context.Sequence, anyMarkers);
                        _records.UndoScopesChanged(context.Sequence, anyMarkers, _versionStore);
                        throw;
                    }
                }
                return marker!;
            }
            finally { owner.ExitApply(undoControl: true); }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Release does not take the apply gate. Validate optimistically, then revalidate
        // under admitted control before starting undo; a release can win between them.
        _versionStore.ValidateMarker(context.Sequence, marker!);
        UndoMarkerValidated?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var undoOwner = _manager.EnterUndoControl(context, out bool undoWasPending, rollback: true);
        try
        {
            try
            {
                _versionStore.BeginRollbackToMarker(context.Sequence, marker!);
            }
            catch
            {
                // Validation failed before the store installed Pending or did any undo.
                // Only this attempt's newly installed retry fence may be removed.
                if (!undoWasPending) { undoOwner.CompletePartialUndo(); }
                throw;
            }

            if (!_versionStore.BuildRollbackLookup(context.Sequence, marker!))
            {
                // Exclusive writer admission makes an empty suffix independent of the gate.
                await _versionStore.RollbackToMarkerUnderGateAsync(context.Sequence, marker!).ConfigureAwait(false);
                _lockManager.RollbackToUndoMarker(context.Sequence, marker!);
                undoOwner.CompletePartialUndo();
                return marker!;
            }
            // Admission freezes this writer's ledger and end before prefix work. Other
            // writers retain the apply gate until physical undo actually needs it.
            await _applyGate.WaitAsync().ConfigureAwait(false);
            _insideApply.Value = true;
            try
            {
                await _versionStore.RollbackToMarkerUnderGateAsync(context.Sequence, marker!).ConfigureAwait(false);
                _lockManager.RollbackToUndoMarker(context.Sequence, marker!);
                undoOwner.CompletePartialUndo();
                return marker!;
            }
            finally
            {
                try
                {
                    if (Volatile.Read(ref _checkpointDeferred) != 0) { RunDeferredCheckpoint(); }
                }
                finally
                {
                    _insideApply.Value = false;
                    _applyGate.Release();
                }
            }
        }
        finally { undoOwner.ExitApply(undoControl: true); }
    }

    /// <summary>
    /// Applies a statement's physical mutations: acquires the apply gate (one
    /// writer statement applies at a time — page conflicts cannot exist), opens
    /// the statement's storage bracket, registers it as the context's current
    /// bracket for the pairing seam, runs the apply, and commits the bracket
    /// non-durably (the transaction's commit record owns durability). Any
    /// failure rolls the statement bracket back physically — the statement
    /// never half-applies.
    /// </summary>
    /// <typeparam name="T">The apply result type.</typeparam>
    /// <param name="context">The transaction the statement belongs to.</param>
    /// <param name="apply">The physical mutations, given the statement bracket.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The apply result.</returns>
    public ValueTask<T> ApplyStatementAsync<T>(
        TransactionContext context,
        Func<StorageTransaction, T> apply,
        CancellationToken cancellationToken = default)
        => ApplyStatementAsync(context, bracket => new ValueTask<T>(apply(bracket)), durable: false, cancellationToken);

    /// <summary>
    /// The asynchronous form of the statement apply, for statement bodies that
    /// drive index maintenance (index mutations are asynchronous surfaces).
    /// <b>Invariant: nothing awaited inside the gate may actually wait.</b> The
    /// only awaits index maintenance performs are unique-key lock acquisitions,
    /// and the executor pre-acquires every unique key lock in its lock phase —
    /// before the gate — so the index's internal acquisition is a same-owner
    /// re-grant that completes synchronously. A genuine wait inside the gate
    /// would be invisible to the lock manager's deadlock detection (the recorded
    /// page-conflict-fallback lesson), so <see cref="LockManager"/> refuses it:
    /// a request that would wait inside the apply fails with
    /// <see cref="InvalidOperationException"/>, which rolls the statement's
    /// bracket back (#1390).
    /// </summary>
    /// <typeparam name="T">The apply result type.</typeparam>
    /// <param name="context">The transaction the statement belongs to.</param>
    /// <param name="apply">The physical mutations, given the statement bracket.</param>
    /// <param name="durable">
    /// When true the bracket commits durably — the self-committing DDL posture
    /// (an index build must not be provable-after-crash only through a user
    /// transaction's later commit record, because its registration in the
    /// catalog file set commits independently). When false the transaction's
    /// commit record owns durability (the DML statement posture).
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The apply result.</returns>
    /// <exception cref="TransactionAbortedException">
    /// The transaction has ended, or its commit, rollback or abort has begun: the
    /// statement was not applied. A transaction can end on another thread while one
    /// of its statements runs (a session closing, a host rolling back a wire
    /// session's transaction); its brackets stop at the end, because a bracket
    /// applied after the rollback's undo would carry a sequence every snapshot then
    /// reads as committed, and nothing would ever undo it.
    /// </exception>
    /// <exception cref="TransactionUndoException">CK04009: partial undo is running or incomplete.</exception>
    /// <exception cref="OperationCanceledException">Gate acquisition or a wait for a memory-only undo control was canceled.</exception>
    /// <remarks>Waits for the database apply gate and any admitted marker create or release.</remarks>
    public async ValueTask<T> ApplyStatementAsync<T>(
        TransactionContext context,
        Func<StorageTransaction, ValueTask<T>> apply,
        bool durable = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(apply);

        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _insideApply.Value = true;

        try
        {
            // Admission under the gate: a statement queued on the gate when its
            // transaction's end began is refused, and an end waits for the one
            // admitted apply of its context to exit before it undoes or commits.
            var owner = _manager.EnterApply(context, cancellationToken);

            try
            {
                var bracket = _storage.BeginTransaction();
                _versionStore.BeginStatement(context.Sequence);

                lock (_sync)
                {
                    _statementBrackets[context.Sequence.Value] = bracket;
                }

                try
                {
                    var result = await apply(bracket).ConfigureAwait(false);
                    bracket.Commit(awaitDurability: durable);
                    return result;
                }
                catch
                {
                    bool restoring = bracket.IsActive;
                    if (bracket.IsActive)
                    {
                        try
                        {
                            bracket.Rollback();
                        }
                        catch
                        {
                            // Checked in the handler, not in an exception filter: a filter runs
                            // before the storage's finally block ends the bracket, so it would
                            // still see the bracket active.
                            if (bracket.IsActive)
                            {
                                throw;
                            }

                            // The bracket ended: its pages are restored and its locks released,
                            // and only its rollback record, which is advisory, was lost. The
                            // statement's own failure is the error the caller must see.
                        }
                    }

                    if (restoring)
                    {
                        try { _versionStore.DiscardStatementSuffix(context.Sequence); }
                        catch
                        {
                            owner.MarkPartialUndoPending();
                            throw;
                        }
                    }
                    throw;
                }
                finally
                {
                    _versionStore.EndStatement();
                    lock (_sync)
                    {
                        _statementBrackets.Remove(context.Sequence.Value);
                    }
                }
            }
            finally
            {
                owner.ExitApply();
            }
        }
        finally
        {
            try
            {
                if (Volatile.Read(ref _checkpointDeferred) != 0) { RunDeferredCheckpoint(); }
            }
            finally
            {
                _insideApply.Value = false;
                _applyGate.Release();
            }
        }
    }

    /// <summary>
    /// Runs the first half of open-time transaction recovery: classifies every
    /// sequence in the recovered journal (<see cref="TransactionRecovery.Analyze"/>),
    /// scrubs every unproven writer's stamps out of the record space (the
    /// open-time form of <see cref="VersionStore.PurgeWriterAsync"/> — one pass
    /// instead of one scan per writer, because the in-memory ledger died with
    /// the process), seeds the prunable set with surviving committed tombstones,
    /// and anchors the prune bound. The caller scrubs any structures of its own
    /// (secondary indexes) with the returned plan, then calls
    /// <see cref="CompleteRecovery"/> — the checkpoint must come last because
    /// the truncation destroys the lifecycle records classification reads.
    /// </summary>
    /// <param name="cancellationToken">Cancels classification and scrub between records.</param>
    /// <returns>The recovery classification, for the caller's own scrub passes.</returns>
    /// <exception cref="OperationCanceledException">Recovery was interrupted; the caller must retain its classification journal.</exception>
    /// <remarks>
    /// The classification also covers the sequences the storage's checkpoint anchor names
    /// (<see cref="Storage.CheckpointActiveTransactions"/>): the writers a checkpoint
    /// truncated the begin records of, which stay classified when the checkpoint's own
    /// record was lost.
    /// </remarks>
    public TransactionRecoveryPlan AnalyzeAndScrub(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = TransactionRecovery.Analyze(_journal, _storage.CheckpointActiveTransactions, cancellationToken);

        _versionStore.ScrubRecovered(plan.Aborted, cancellationToken);

        // Anchor the prune bound above every pre-restart stamp: the fresh
        // manager has assigned nothing yet, so its idle oldest-active bound
        // trails the storage's sequence namespace — a reserved sequence is a
        // proven ceiling over every stamp the record space can carry.
        _recoveredSequenceFloor = new TransactionSequence((ulong)_storage.ReserveTransactionSequence());

        TransactionEventSource.Log.RecoveryAnalyzed(_storage, plan);
        return plan;
    }

    /// <summary>
    /// Completes open-time recovery: starts the journal clean with the
    /// open-time checkpoint deferred by the engine. No
    /// logical transactions exist yet, so the active list is empty.
    /// </summary>
    /// <param name="cancellationToken">Observed before entering the atomic checkpoint.</param>
    /// <exception cref="OperationCanceledException">The token was canceled before the checkpoint.</exception>
    public void CompleteRecovery(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _storage.Checkpoint();
    }

    /// <summary>
    /// Checkpoints the data storage while logical transactions may be in flight:
    /// the checkpoint record and the storage's checkpoint anchor carry the sequences
    /// of every in-flight <em>writer</em> — a transaction that applied a statement and
    /// so can have stamped row versions, including a rolled-back writer whose undo is
    /// deferred — captured atomically with the truncation (no lifecycle record can land
    /// in between), so recovery classification stays sound. A reader stamps nothing and
    /// is not listed; if it applies a statement after the checkpoint, its sequence is
    /// first announced again with a begin record, so the journal names it before its
    /// first stamp exists.
    /// </summary>
    /// <exception cref="StorageTransactionException">
    /// A storage-level bracket is still active, or the call came from inside a statement apply
    /// of this coordinator (<see cref="ApplyStatementAsync{T}(TransactionContext, Func{StorageTransaction, ValueTask{T}}, bool, CancellationToken)"/>),
    /// whose apply gate the checkpoint would wait for forever.
    /// </exception>
    public void Checkpoint() => Checkpoint(CancellationToken.None);

    /// <summary>
    /// Checkpoints the data storage (see <see cref="Checkpoint()"/>), first waiting for the
    /// statement apply gate, so no statement bracket of this coordinator is open when the
    /// storage counts its active brackets.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for the apply gate.</param>
    /// <exception cref="StorageTransactionException">
    /// A storage-level bracket that does not go through the apply gate is still active, or the
    /// call came from inside a statement apply of this coordinator.
    /// </exception>
    /// <exception cref="OperationCanceledException">The wait for the apply gate was canceled.</exception>
    /// <remarks>
    /// A checkpoint truncates the journal only while no storage bracket is active. Every
    /// statement, undo, prune and recovery-scrub bracket of the coordinator runs under the apply
    /// gate, so without taking it a checkpoint under a sustained write load found a bracket
    /// open nearly every time, was refused as busy, and the journal grew without bound. Taking
    /// the gate waits for the statement already applying, however long it runs (an index build
    /// or an <c>INSERT ... SELECT</c> applies in one bracket); a caller that checkpoints several
    /// databases in turn uses <see cref="TryCheckpoint"/>, so one long statement does not hold
    /// up the others (#1254).
    /// </remarks>
    public void Checkpoint(CancellationToken cancellationToken)
    {
        ThrowIfInsideApply();
        _applyGate.Wait(cancellationToken);
        try
        {
            Volatile.Write(ref _checkpointDeferred, 0);
            _log.CheckpointUnderGate(_storage);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// Checkpoints the data storage (see <see cref="Checkpoint()"/>) if the statement apply gate
    /// can be taken within <paramref name="gateTimeout"/>. Otherwise the checkpoint is deferred
    /// to the statement holding the gate, which runs it before it releases the gate, and the call
    /// returns false at once: it never waits longer than <paramref name="gateTimeout"/>.
    /// </summary>
    /// <param name="gateTimeout">
    /// The longest the call waits for the gate; <see cref="TimeSpan.Zero"/> takes it only when it
    /// is free.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for the apply gate.</param>
    /// <returns>
    /// True when the checkpoint ran on this call; false when a statement held the gate, which then
    /// runs the checkpoint as it ends.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="gateTimeout"/> is negative (other than <see cref="Timeout.InfiniteTimeSpan"/>) or exceeds
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    /// <exception cref="StorageTransactionException">
    /// A storage-level bracket that does not go through the apply gate is still active, or the
    /// call came from inside a statement apply of this coordinator.
    /// </exception>
    /// <exception cref="OperationCanceledException">The wait for the apply gate was canceled.</exception>
    /// <exception cref="Exception">
    /// The failure of a checkpoint a statement ran for an earlier deferred request, thrown once,
    /// before this call does anything else.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The engines' checkpoint workers visit every database of an engine in turn on one thread.
    /// Waiting for the gate without a bound let one database's long statement (a large
    /// <c>CREATE INDEX</c>, <c>UPDATE</c> or <c>INSERT ... SELECT</c>) park that thread, and every
    /// other database's journal grew past its size meanwhile (#1254 review: 130 times a 4 MiB
    /// size in six seconds). Any bounded wait fixes that, but trades the busy database against
    /// the others: a short one rarely catches the end of a long statement, a long one lets the
    /// others overshoot. Deferring needs no trade: the worker moves on at once, and the busy
    /// database is checkpointed the moment its statement ends, before the next statement takes
    /// the gate, whether the statement ran for a millisecond or a minute.
    /// </para>
    /// <para>
    /// The deferred checkpoint runs on the thread of the statement that ends, which waits for it
    /// as the next statement would have waited at the gate. It never fails that statement: a
    /// storage bracket still open outside the gate leaves the request for the next statement, an
    /// offline storage drops it, and any other failure is thrown by the next call of this method,
    /// so the engine's worker records it.
    /// </para>
    /// </remarks>
    public bool TryCheckpoint(TimeSpan gateTimeout, CancellationToken cancellationToken)
    {
        ThrowIfInsideApply();
        Interlocked.Exchange(ref _deferredCheckpointFailure, null)?.Throw();

        if (!_applyGate.Wait(gateTimeout, cancellationToken))
        {
            // Set after the failed wait: a statement that releases the gate before this write is
            // seen leaves the request for the next statement, or for the worker's next look.
            Volatile.Write(ref _checkpointDeferred, 1);
            TransactionEventSource.Log.CheckpointDeferred(_storage);
            return false;
        }

        try
        {
            Volatile.Write(ref _checkpointDeferred, 0);
            _log.CheckpointUnderGate(_storage);
            return true;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// Runs the checkpoint <see cref="TryCheckpoint"/> deferred to the statement that holds the
    /// apply gate, under that gate, as the statement ends. Never throws: the statement's own
    /// outcome is already decided.
    /// </summary>
    private void RunDeferredCheckpoint()
    {
        try
        {
            _log.CheckpointUnderGate(_storage);
            Volatile.Write(ref _checkpointDeferred, 0);
        }
        catch (StorageTransactionException exception) when (exception.Code != StorageTransactionErrorCode.ForeignStorage)
        {
            // A storage bracket outside the apply gate is open; the next statement to end, or
            // the worker's next look, retries.
            TransactionEventSource.Log.DeferredCheckpointSkipped(_storage, deferredCheckpointBracketOpen);
        }
        catch (StorageOfflineException)
        {
            // A durable flush failed and took the storage offline (#1243): nothing more is
            // written, and every later operation on the database is refused.
            Volatile.Write(ref _checkpointDeferred, 0);
            TransactionEventSource.Log.DeferredCheckpointSkipped(_storage, deferredCheckpointStorageOffline);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Volatile.Write(ref _checkpointDeferred, 0);
            Volatile.Write(ref _deferredCheckpointFailure, ExceptionDispatchInfo.Capture(exception));
            TransactionEventSource.Log.DeferredCheckpointFailed(_storage, exception);
        }
    }

    // The reasons a deferred checkpoint did not run, as the DeferredCheckpointSkipped event names them.
    private const string deferredCheckpointBracketOpen = "BracketOpen";
    private const string deferredCheckpointStorageOffline = "StorageOffline";

    /// <summary>
    /// Refuses maintenance asked for from inside one of this coordinator's statement applies:
    /// the apply gate is not reentrant, so waiting for it there would never end.
    /// </summary>
    private void ThrowIfInsideApply()
    {
        if (_insideApply.Value)
        {
            throw new StorageTransactionException(
                ErrorMessages.CoordinatorThrowIfInsideApplyFailure);
        }
    }

    /// <summary>
    /// Runs one maintenance pass for the version-purge worker: retries any
    /// aborted writer whose undo previously failed, then prunes record and index versions below
    /// the safe bound — the minimum snapshot floor of every active transaction
    /// (a long-running snapshot pins its view), or the next sequence when idle.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the pass between writers and prune batches; a rolled-back writer's
    /// undo, once started, runs to completion.
    /// </param>
    /// <returns>The number of record versions and index entries physically reclaimed or undone.</returns>
    /// <remarks>
    /// A rollback whose undo failed ended its transaction but left the writer in
    /// flight, holding its locks. The pass retries that undo through the manager,
    /// which releases the writer — abort record, active table, locks — as soon as
    /// the undo completes. A retry that fails again is rethrown at the end of the pass,
    /// after every deferred writer was attempted and the rest of the pass ran, so a
    /// writer whose undo keeps failing does not stop the reclamation of committed
    /// tombstones below it; the writer waits for the next pass. A writer queued by a
    /// direct <see cref="VersionStore.PurgeWriterAsync"/> caller is retried the same way,
    /// and the prune reclaims every tombstone it can read before it throws for one whose
    /// version page it cannot read (a failed checksum or an I/O error, #1342), which keeps
    /// that tombstone for the next pass.
    /// </remarks>
    /// <exception cref="StorageTransactionException">Called inside this coordinator's statement apply.</exception>
    public long RunVersionPurgePass(CancellationToken cancellationToken)
    {
        ThrowIfInsideApply();
        // Timed only while a listener takes the pass event (event-source.md, rule 9).
        bool timed = TransactionEventSource.Log.IsEnabled(EventLevel.Verbose, TransactionEventSource.Keywords.Purge);
        long started = timed ? Stopwatch.GetTimestamp() : 0;
        long total = 0;
        ExceptionDispatchInfo? deferredFailure = null;

        try
        {
            total += _manager.RetryDeferredUndoAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not ObjectDisposedException && !cancellationToken.IsCancellationRequested)
        {
            // The writers still deferred keep their place in the active table, so the safe
            // prune bound below stays under their sequences: pruning cannot reach their
            // versions, only committed tombstones older than them.
            deferredFailure = ExceptionDispatchInfo.Capture(exception);
        }

        // Writers queued by a direct PurgeWriterAsync caller; the manager owns the rest. A failed
        // undo requeues its writer and is rethrown at the end of the pass, as a deferred one is,
        // so a version page that cannot be read does not stop the prune below (#1342).
        foreach (ulong writer in _versionStore.PendingAbortedPurges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_manager.IsUndoDeferred(writer))
            {
                continue;
            }

            try
            {
                total += _versionStore.PurgeWriterAsync(new TransactionSequence(writer), cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not ObjectDisposedException && !cancellationToken.IsCancellationRequested)
            {
                deferredFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var bound = GetSafePruneBound();
        try
        {
            total += _versionStore.PruneAsync(bound, cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not (ObjectDisposedException or OperationCanceledException or OutOfMemoryException)
            && !cancellationToken.IsCancellationRequested)
        {
            // A damaged record page must not prevent independent index reclamation.
            deferredFailure ??= ExceptionDispatchInfo.Capture(exception);
        }

        if (_indexPruner is not null)
        {
            try
            {
                _applyGate.Wait(cancellationToken);
                try
                {
                    var pending = _indexPruner(bound, cancellationToken);
                    total += pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
                }
                finally { _applyGate.Release(); }
            }
            catch (Exception exception) when (exception is not (ObjectDisposedException or OperationCanceledException or OutOfMemoryException)
                && !cancellationToken.IsCancellationRequested)
            {
                deferredFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        if (timed)
        {
            TransactionEventSource.Log.VersionPurgePass(_storage, total, started);
        }

        deferredFailure?.Throw();
        return total;
    }

    /// <summary>Gets a conservative exclusive bound below which committed tombstones are no longer visible to any live or future snapshot.</summary>
    /// <remarks>Includes snapshots still pinned by a statement whose manager transaction ended.</remarks>
    public TransactionSequence PruneBound => GetSafePruneBound();

    /// <summary>
    /// Computes the prune bound no live or future snapshot can see below: the
    /// manager's <see cref="TransactionManager.PruneBound"/>, the minimum of every
    /// active sequence and of every active snapshot or serializable transaction's
    /// <see cref="TransactionSnapshot.Minimum"/>, taken atomically with
    /// <see cref="TransactionManager.BeginAsync"/>. The oldest active sequence alone
    /// is NOT safe under load: a live snapshot can hold a <em>lower</em> minimum
    /// (it captured while an older, since-committed transaction was still in
    /// flight) and must keep seeing versions that transaction's tombstones would
    /// otherwise free.
    /// </summary>
    /// <remarks>
    /// The open transactions this coordinator tracks are not enough on their own: one
    /// joins them only after the manager began it, and a pass in between would prune
    /// below its snapshot's minimum (#1342 review). They are still folded in, which
    /// covers one the manager already ended while its statement still reads.
    /// </remarks>
    private TransactionSequence GetSafePruneBound()
    {
        var bound = _manager.PruneBound;

        // After a reopen the fresh manager's idle bound trails the storage's
        // sequence namespace; the recovered floor is a proven ceiling over
        // every pre-restart stamp.
        if (_recoveredSequenceFloor > bound)
        {
            bound = _recoveredSequenceFloor;
        }

        lock (_sync)
        {
            foreach (var context in _openContexts.Values)
            {
                var minimum = context.Snapshot.Minimum;
                if (minimum < bound) { bound = minimum; }
            }
        }

        return bound;
    }

    /// <summary>
    /// Disposes the coordinator: the manager aborts every still-active
    /// transaction (purging its stamps through the version store's ledger)
    /// and waits for every commit or rollback already running, before the
    /// storage closes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rolled-back writer whose undo still fails at disposal is rethrown after every
    /// other transaction was aborted. Its versions are still in the record space, and
    /// only the next open's recovery scrub can remove them, which it does only if the
    /// journal still classifies the writer at that open. The storage's clean close would
    /// not keep it so: an idle storage closes with a checkpoint that truncates the journal
    /// and lists no active transaction, which erases the writer's begin record and the
    /// checkpoint entries carrying it.
    /// </para>
    /// <para>
    /// So, before rethrowing, the coordinator begins one storage bracket per such writer,
    /// adopting the writer's own sequence (<see cref="Storage.BeginTransaction(long)"/>,
    /// which writes nothing). The writer is then in flight at the physical layer too, and
    /// the storage's close takes its non-idle path: it flushes pages and journal and does
    /// not truncate. The next open finds the writer without a commit record, classifies it
    /// as aborted and scrubs its versions, exactly as after a crash. The owner closes the
    /// storage as usual, whether or not this method throws.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _manager.DisposeAsync().ConfigureAwait(false);
        }
        catch when (IsStorageOffline)
        {
            // The storage went offline after a failed durable flush (#1243): it refuses every
            // write, so the aborts' undo could not run, and none is needed. The storage closes
            // without writing, and the next open's recovery classifies every writer without a
            // commit record, from the journal and the checkpoint anchor, as aborted and scrubs it.
        }
        catch
        {
            KeepDeferredWritersInFlight();
            throw;
        }
        finally
        {
            List<TransactionContext>? markedEnds = null;
            lock (_sync)
            {
                _statementBrackets.Clear();

                // Each context still tracked leaves the current-transactions gauge here, once:
                // a commit or rollback that untracks it later finds it gone.
                foreach (var context in _openContexts.Values)
                {
                    TransactionEventSource.Log.TransactionUntracked(context);
                    if (context.HadUndoMarkers) { (markedEnds ??= new()).Add(context); }
                }

                _openContexts.Clear();
            }
            if (markedEnds is not null)
            { foreach (var context in markedEnds) { _records.TransactionEnded(context); } }
        }
    }

    // The brackets are deliberately never completed: the storage closes with them active,
    // and they hold no page and wrote no record, so its recovery has nothing of theirs to undo.
    private void KeepDeferredWritersInFlight()
    {
        foreach (ulong writer in _manager.GetDeferredUndoWriters())
        {
            _ = _storage.BeginTransaction((long)writer);
        }
    }

    // A commit or rollback refused before it started (a canceled token, or another end
    // already running) leaves the context active: it stays tracked, so its snapshot
    // keeps bounding the prune and its statement bracket stays resolvable.
    private void UntrackEnded(TransactionContext context)
    {
        // A rejected foreign end may have the same sequence as a local writer. Only
        // this manager's contexts can drive tracking or model-ledger cleanup. Preserve
        // the manager's argument failure too when a public end receives null.
        if (context is null || !ReferenceEquals(context.Manager, _manager)) { return; }
        if (context.State != TransactionState.Active && context.HadUndoMarkers) { _records.TransactionEnded(context); }
        if (context.State == TransactionState.Active)
        {
            return;
        }

        lock (_sync)
        {
            _statementBrackets.Remove(context.Sequence.Value);
            if (_openContexts.Remove(context.Sequence.Value))
            {
                TransactionEventSource.Log.TransactionUntracked(context);
            }
        }
    }

    /// <summary>
    /// The manager's journal-bound transaction log, gated so lifecycle appends
    /// and checkpoint truncation are mutually exclusive: the checkpoint captures
    /// the writer list and truncates under the same gate no append can
    /// interleave with, which is what makes recovery classification sound across
    /// truncation. Durability: commit appends the record under the gate and
    /// flushes outside it — if a checkpoint truncated past the record first, the
    /// checkpoint's own durable flush already covered the outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only writers are anchored (#1242).</b> Recovery must classify every transaction
    /// whose row versions can be in the data pages, and only a transaction that applied a
    /// statement can have stamped one; readers append no lifecycle records (#1280).
    /// So a checkpoint lists the active writers alone, which keeps the storage's checkpoint
    /// anchor small under read-heavy load.
    /// </para>
    /// <para>
    /// A transaction that was a reader at a checkpoint and applies a statement afterwards is
    /// named by neither the journal nor that checkpoint. Before its first statement bracket
    /// begins, <see cref="EnterWriter"/> appends its begin record under the gate, so a crash
    /// before its commit still finds it unproven and scrubs its versions. Later applies need
    /// no second begin: the journal or the checkpoint anchor keeps naming the writer.
    /// </para>
    /// </remarks>
    [global::System.Diagnostics.DebuggerDisplay("GatedJournalLog")]
    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Transactions.TransactionCoordinator.GatedJournalLog.DebugView))]
    private sealed class GatedJournalLog : TransactionLog
    {
        private readonly TransactionCoordinator _coordinator;

        // The active transactions that applied a statement: the only ones that can have
        // stamped row versions, so the only ones a checkpoint must list. A rolled-back
        // writer whose undo is deferred stays here until its abort record is appended.
        private readonly ConcurrentDictionary<long, byte> _writers = new();

        // Lock order: apply admission/checkpoint holds _applyGate before this gate;
        // checkpoint then takes Storage's transaction/header locks and pool flush gate.
        // Pool I/O releases its monitor before taking journal/stream locks.
        // Enrollment and lifecycle appends take this gate before the journal's monitor.
        // No manager/context lock is held here, and durability waits run outside this gate.
        private readonly object _gate = new();

        internal GatedJournalLog(TransactionCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        public override ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Readers retain their storage-counter sequence and snapshot, but have nothing
            // to classify after a crash. The first apply enrolls them under the append gate.
            return default;
        }

        /// <summary>
        /// Records that a transaction is about to apply a statement and so may stamp row
        /// versions: append its begin record before the first physical bracket, and from now
        /// on every checkpoint lists it. A reader that outlived a checkpoint enrolls here too.
        /// </summary>
        /// <param name="sequence">The transaction's sequence.</param>
        public override void EnterWriter(TransactionSequence sequence)
        {
            lock (_gate)
            {
                long writer = (long)sequence.Value;
                if (_writers.ContainsKey(writer))
                {
                    return;
                }

                _coordinator._journal.AppendBegin(writer);
                _writers.TryAdd(writer, 0);
            }
        }

        public override ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // End has drained every admitted apply before calling the log. No later apply
            // can enroll this sequence, so absence is stable without the checkpoint gate.
            // The concurrent table permits this read; all mutations still hold _gate.
            if (!_writers.ContainsKey((long)sequence.Value))
            {
                return default;
            }

            long lsn;
            lock (_gate)
            {
                if (!_writers.ContainsKey((long)sequence.Value))
                {
                    Forget((long)sequence.Value);
                    return default;
                }

                lsn = _coordinator._journal.AppendCommit((long)sequence.Value);
                Forget((long)sequence.Value);
            }

            // Durability outside the gate: if a checkpoint truncated past the
            // record under a durable mode it already flushed everything durably,
            // and EnsureDurable on an already-durable LSN is a no-op. A storage
            // under None claims no durability here, and an initialized storage
            // never enters or leaves None (owner decision 26 of 2026-10-06), so a
            // durable wait never meets a truncation made without a data fsync.
            // By journal ordering this flush also covers every statement bracket
            // the transaction committed non-durably.
            try
            {
                _coordinator._storage.EnsureCommitDurable(lsn);
            }
            catch (Exception exception)
            {
                // The record is in the journal and the sequence has left the checkpoint
                // list, so recovery and every later checkpoint read the transaction as
                // committed: it can no longer abort. The failed flush took the storage
                // offline, so the reopen's recovery decides whether it survives (#1243).
                throw new TransactionCommitUnconfirmedException(JournalTransactionLog.UnconfirmedMessage(sequence), exception);
            }

            return default;
        }

        public override ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // As at commit, end admission has closed and drained the transaction's applies.
            if (!_writers.ContainsKey((long)sequence.Value))
            {
                return default;
            }

            lock (_gate)
            {
                if (!_writers.ContainsKey((long)sequence.Value))
                {
                    Forget((long)sequence.Value);
                    return default;
                }

                try
                {
                    _coordinator.BeforeAbortRecord?.Invoke((long)sequence.Value);
                    _coordinator._journal.AppendRollback((long)sequence.Value);
                }
                finally
                {
                    // The manager appends the abort record only once the writer's undo
                    // completed, so nothing of the writer is left for recovery to scrub
                    // and later checkpoints need not carry it — whether or not the
                    // advisory record itself was written (#1226).
                    Forget((long)sequence.Value);
                }
            }

            return default;
        }

        /// <summary>
        /// Checkpoints the storage with the writer list captured under the append gate,
        /// so no lifecycle record can land between the capture and the truncation.
        /// </summary>
        internal void CheckpointUnderGate(Storage storage)
        {
            lock (_gate)
            {
                Span<long> writers = _writers.Count <= 64
                    ? stackalloc long[_writers.Count]
                    : new long[_writers.Count];

                int index = 0;
                foreach (var writer in _writers)
                {
                    writers[index++] = writer.Key;
                }

                writers.Sort();

                _coordinator.BeforeCheckpoint?.Invoke(writers.ToArray());
                storage.Checkpoint(writers);
            }
        }

        private void Forget(long sequence)
        {
            _writers.TryRemove(sequence, out _);
        }

        private sealed class DebugView
        {
            public DebugView(global::Assimalign.Cohesion.Database.Transactions.TransactionCoordinator.GatedJournalLog value)
            {
                _ = value;
            }
        }
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Transactions.TransactionCoordinator _target;

        public DebugView(global::Assimalign.Cohesion.Database.Transactions.TransactionCoordinator value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Transactions.TransactionManager Manager => _target.Manager;
        public global::Assimalign.Cohesion.Database.Transactions.LockManager LockManager => _target.LockManager;
        public global::Assimalign.Cohesion.Database.Transactions.RecordSpaceVersionStore VersionStore => _target.VersionStore;
    }
}
