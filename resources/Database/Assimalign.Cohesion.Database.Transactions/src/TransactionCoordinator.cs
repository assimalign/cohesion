using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
public sealed class TransactionCoordinator : IAsyncDisposable
{
    private readonly Storage _storage;
    private readonly StorageJournal _journal;
    private readonly TransactionManager _manager;
    private readonly LockManager _lockManager;
    private readonly RecordSpaceVersionStore _versionStore;
    private readonly GatedJournalLog _log;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    // True in the async flow of a statement apply that holds the apply gate, which is not
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

    /// <summary>
    /// Creates the composition with the clock the deferred-undo retry schedule reads (tests).
    /// </summary>
    internal TransactionCoordinator(Storage storage, StorageJournal journal, TransactionRecordSpace records, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(records);
        _storage = storage;
        _journal = journal;

        // Fully qualified: the coordinator's LockManager property shadows the
        // factory class name inside this scope.
        var locks = Transactions.LockManager.Create();
        _versionStore = new RecordSpaceVersionStore(storage, records, _applyGate);
        _log = new GatedJournalLog(this);

        // The manager over the gated journal log: the statement apply admits a bracket
        // only for a context the manager still holds open, and the version-purge pass
        // drives its deferred-undo retry. It releases a transaction's locks through the
        // lock manager's unfiltered release; engine code shares the same lock manager in
        // its engine mode, whose release-all leaves a tracked transaction to the manager
        // (see LockManager).
        _manager = new TransactionManager(
            _log,
            locks,
            _versionStore,
            ReserveSequence,
            time);
        locks.EnterEngineMode(_manager.IsTracked);
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
    /// storage's counter (one namespace) and appends the begin record through
    /// the journal-bound log. Physical brackets are per statement — see the
    /// class remarks.
    /// </summary>
    /// <param name="isolationLevel">The isolation level the transaction runs under.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The transaction context.</returns>
    public async ValueTask<TransactionContext> BeginAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        var context = await _manager.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _openContexts[context.Sequence.Value] = context;
        }

        return context;
    }

    /// <summary>
    /// Commits the transaction through the manager: the journal-bound log
    /// appends the commit record and awaits durability (which, by journal
    /// ordering, also makes every statement bracket's records durable) while
    /// the transaction is still in the manager's active table; the version
    /// store then retains the writer's tombstones for pruning.
    /// </summary>
    /// <param name="context">The transaction to commit.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
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
        }
        catch (TransactionCommitUnconfirmedException)
        {
            _versionStore.OnCommitted(context.Sequence);
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
    /// tombstones cleared), the abort record is appended, and locks release.
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
        }
        finally
        {
            UntrackEnded(context);
        }
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
    /// page-conflict-fallback lesson).
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
            var owner = _manager.EnterApply(context);

            try
            {
                // From here on the transaction can stamp row versions, so every checkpoint
                // until it ends must keep it classifiable (and the journal must name it now
                // if the last checkpoint truncated its begin record while it was a reader).
                _log.EnterWriter((long)context.Sequence.Value);

                var bracket = _storage.BeginTransaction();

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

                    throw;
                }
                finally
                {
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
            if (Volatile.Read(ref _checkpointDeferred) != 0)
            {
                RunDeferredCheckpoint();
            }

            _insideApply.Value = false;
            _applyGate.Release();
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
    /// <returns>The recovery classification, for the caller's own scrub passes.</returns>
    /// <remarks>
    /// The classification also covers the sequences the storage's checkpoint anchor names
    /// (<see cref="Storage.CheckpointActiveTransactions"/>): the writers a checkpoint
    /// truncated the begin records of, which stay classified when the checkpoint's own
    /// record was lost.
    /// </remarks>
    public TransactionRecoveryPlan AnalyzeAndScrub()
    {
        var plan = TransactionRecovery.Analyze(_journal, _storage.CheckpointActiveTransactions);

        _versionStore.ScrubRecovered(plan.Aborted);

        // Anchor the prune bound above every pre-restart stamp: the fresh
        // manager has assigned nothing yet, so its idle oldest-active bound
        // trails the storage's sequence namespace — a reserved sequence is a
        // proven ceiling over every stamp the record space can carry.
        _recoveredSequenceFloor = new TransactionSequence((ulong)_storage.ReserveTransactionSequence());

        return plan;
    }

    /// <summary>
    /// Completes open-time recovery: starts the journal clean with the
    /// open-time checkpoint deferred by the engine. No
    /// logical transactions exist yet, so the active list is empty.
    /// </summary>
    public void CompleteRecovery()
    {
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
        catch (StorageTransactionException)
        {
            // A storage bracket outside the apply gate is open; the next statement to end, or
            // the worker's next look, retries.
        }
        catch (StorageOfflineException)
        {
            // A durable flush failed and took the storage offline (#1243): nothing more is
            // written, and every later operation on the database is refused.
            Volatile.Write(ref _checkpointDeferred, 0);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Volatile.Write(ref _checkpointDeferred, 0);
            Volatile.Write(ref _deferredCheckpointFailure, ExceptionDispatchInfo.Capture(exception));
        }
    }

    /// <summary>
    /// Refuses a checkpoint asked for from inside one of this coordinator's statement applies:
    /// the apply gate is not reentrant, so waiting for it there would never end.
    /// </summary>
    private void ThrowIfInsideApply()
    {
        if (_insideApply.Value)
        {
            throw new StorageTransactionException(
                "A checkpoint cannot run inside a statement apply: the statement holds the apply gate the checkpoint waits for. " +
                "Checkpoint after the statement completes.");
        }
    }

    /// <summary>
    /// Runs one maintenance pass for the version-purge worker: retries any
    /// aborted writer whose undo previously failed, then prunes versions below
    /// the safe bound — the minimum snapshot floor of every open transaction
    /// (a long-running snapshot pins its view), or the manager's oldest-active
    /// bound when idle.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the pass between writers and prune batches; a rolled-back writer's
    /// undo, once started, runs to completion.
    /// </param>
    /// <returns>The number of versions physically reclaimed or undone.</returns>
    /// <remarks>
    /// A rollback whose undo failed ended its transaction but left the writer in
    /// flight, holding its locks. The pass retries that undo through the manager,
    /// which releases the writer — abort record, active table, locks — as soon as
    /// the undo completes. A retry that fails again is rethrown at the end of the pass,
    /// after every deferred writer was attempted and the rest of the pass ran, so a
    /// writer whose undo keeps failing does not stop the reclamation of committed
    /// tombstones below it; the writer waits for the next pass.
    /// </remarks>
    public long RunVersionPurgePass(CancellationToken cancellationToken)
    {
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

        // Writers queued by a direct PurgeWriterAsync caller; the manager owns the rest.
        foreach (ulong writer in _versionStore.PendingAbortedPurges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_manager.IsUndoDeferred(writer))
            {
                continue;
            }

            total += _versionStore.PurgeWriterAsync(new TransactionSequence(writer), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
        }

        cancellationToken.ThrowIfCancellationRequested();

        total += _versionStore.PruneAsync(GetSafePruneBound(), cancellationToken)
            .AsTask().GetAwaiter().GetResult();

        deferredFailure?.Throw();
        return total;
    }

    /// <summary>
    /// Computes the prune bound no live or future snapshot can see below: the
    /// minimum <see cref="TransactionSnapshot.Minimum"/> across open
    /// transactions, or <see cref="TransactionManager.OldestActive"/> when
    /// none are open. The manager's bound alone is NOT safe under load: a live
    /// snapshot can hold a <em>lower</em> minimum than the oldest active
    /// sequence (it captured while an older, since-committed transaction was
    /// still in flight) and must keep seeing versions that transaction's
    /// tombstones would otherwise free.
    /// </summary>
    private TransactionSequence GetSafePruneBound()
    {
        var bound = _manager.OldestActive;

        // After a reopen the fresh manager's idle bound trails the storage's
        // sequence namespace; the recovered floor is a proven ceiling over
        // every pre-restart stamp.
        if (_recoveredSequenceFloor > bound)
        {
            bound = _recoveredSequenceFloor;
        }

        foreach (var context in GetOpenContexts())
        {
            var minimum = context.Snapshot.Minimum;

            if (minimum < bound)
            {
                bound = minimum;
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
            lock (_sync)
            {
                _statementBrackets.Clear();
                _openContexts.Clear();
            }
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
        if (context.State == TransactionState.Active)
        {
            return;
        }

        lock (_sync)
        {
            _statementBrackets.Remove(context.Sequence.Value);
            _openContexts.Remove(context.Sequence.Value);
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
    /// statement can have stamped one; a reader's begin record may go with the truncation.
    /// So a checkpoint lists the active writers alone, which keeps the storage's checkpoint
    /// anchor small under read-heavy load.
    /// </para>
    /// <para>
    /// A transaction that was a reader at a checkpoint and applies a statement afterwards is
    /// no longer named by the journal: its begin record was truncated and the checkpoint did
    /// not list it. Before its first statement bracket begins, <see cref="EnterWriter"/>
    /// therefore appends its begin record again, under the gate, so a crash before its commit
    /// still finds it unproven and scrubs its versions. A transaction whose begin record is
    /// in the current journal, or that the last checkpoint listed, needs no second one.
    /// </para>
    /// </remarks>
    private sealed class GatedJournalLog : TransactionLog
    {
        private readonly TransactionCoordinator _coordinator;
        private readonly HashSet<long> _activeSequences = new();

        // The active transactions that applied a statement: the only ones that can have
        // stamped row versions, so the only ones a checkpoint must list. A rolled-back
        // writer whose undo is deferred stays here until its abort record is appended.
        private readonly HashSet<long> _writers = new();

        // The active transactions the journal names since the last checkpoint: begun since
        // then, announced again since then, or listed by that checkpoint (its writers).
        private readonly HashSet<long> _journaled = new();
        private readonly object _gate = new();

        internal GatedJournalLog(TransactionCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        public override ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                _coordinator._journal.AppendBegin((long)sequence.Value);
                _activeSequences.Add((long)sequence.Value);
                _journaled.Add((long)sequence.Value);
            }

            return default;
        }

        /// <summary>
        /// Records that a transaction is about to apply a statement and so may stamp row
        /// versions: from now on every checkpoint lists it. When the journal no longer names
        /// it — a checkpoint truncated its begin record while it was a reader — its begin
        /// record is appended again first, so the journal names it before its first stamp.
        /// </summary>
        /// <param name="sequence">The transaction's sequence.</param>
        internal void EnterWriter(long sequence)
        {
            lock (_gate)
            {
                if (_writers.Contains(sequence) || !_activeSequences.Contains(sequence))
                {
                    return;
                }

                if (!_journaled.Contains(sequence))
                {
                    _coordinator._journal.AppendBegin(sequence);
                    _journaled.Add(sequence);
                }

                _writers.Add(sequence);
            }
        }

        public override ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long lsn;
            lock (_gate)
            {
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

            lock (_gate)
            {
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
                foreach (long sequence in _writers)
                {
                    writers[index++] = sequence;
                }

                writers.Sort();

                try
                {
                    _coordinator.BeforeCheckpoint?.Invoke(writers.ToArray());
                    storage.Checkpoint(writers);
                }
                finally
                {
                    // Whether or not the truncation ran (it may have, even when the
                    // checkpoint record then failed), only the writers it listed are still
                    // named for certain; any reader that becomes a writer is announced again.
                    _journaled.Clear();
                    _journaled.UnionWith(_writers);
                }
            }
        }

        private void Forget(long sequence)
        {
            _activeSequences.Remove(sequence);
            _writers.Remove(sequence);
            _journaled.Remove(sequence);
        }
    }
}
