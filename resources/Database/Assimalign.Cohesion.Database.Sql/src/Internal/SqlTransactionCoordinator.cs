using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// The per-database MVCC composition (area DESIGN §3.8): one
/// <see cref="ITransactionManager"/> + <see cref="ILockManager"/> +
/// <see cref="SqlRecordSpaceVersionStore"/> over the database's data storage,
/// with the manager's transaction log bound to the storage's write-ahead
/// journal. The coordinator owns the sequence-space unification (the manager
/// allocates from the storage's counter, so the journal carries one sequence
/// namespace), the per-statement physical brackets and their apply gate, the
/// pairing seam (<see cref="IStorageTransactionSource"/> resolves a context's
/// current statement bracket), and the checkpoint interlock that keeps
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
/// user-visible conflict surface: row locks (acquired <em>before</em> the gate,
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
internal sealed class SqlTransactionCoordinator : IStorageTransactionSource, IAsyncDisposable
{
    private readonly SqlStorage _storage;
    private readonly ITransactionManager _manager;
    private readonly ILockManager _lockManager;
    private readonly SqlRecordSpaceVersionStore _versionStore;
    private readonly GatedJournalLog _log;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly Dictionary<ulong, IStorageTransaction> _statementBrackets = new();
    private readonly Dictionary<ulong, ITransactionContext> _openContexts = new();
    private readonly object _sync = new();
    private TransactionSequence _recoveredSequenceFloor;

    internal SqlTransactionCoordinator(SqlStorage storage)
    {
        _storage = storage;

        // Fully qualified: the coordinator's LockManager property shadows the
        // factory class name inside this scope.
        _lockManager = Transactions.LockManager.Create();
        _versionStore = new SqlRecordSpaceVersionStore(storage, _applyGate);
        _log = new GatedJournalLog(this);
        _manager = TransactionManager.Create(
            _log,
            _lockManager,
            _versionStore,
            () => new TransactionSequence((ulong)storage.ReserveTransactionSequence()));
    }

    /// <summary>
    /// Gets the transaction manager sessions begin their contexts on.
    /// </summary>
    internal ITransactionManager Manager => _manager;

    /// <summary>
    /// Gets the lock manager arbitrating row- and object-grain write conflicts.
    /// </summary>
    internal ILockManager LockManager => _lockManager;

    /// <summary>
    /// Gets the version store: the ledger over the record space that makes
    /// logical undo and version pruning executable.
    /// </summary>
    internal SqlRecordSpaceVersionStore VersionStore => _versionStore;

    /// <summary>
    /// Gets the number of statement brackets currently applying (test
    /// observability; zero whenever no statement is mid-apply).
    /// </summary>
    internal int PairedTransactionCount
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
    internal IReadOnlyList<ITransactionContext> GetOpenContexts()
    {
        lock (_sync)
        {
            return [.. _openContexts.Values];
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolves the context's <em>current statement bracket</em>: index and
    /// catalog mutations made on the transaction's behalf ride the same
    /// statement-scoped write-ahead bracket as its row mutations.
    /// </remarks>
    public IStorageTransaction GetStorageTransaction(ITransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_sync)
        {
            if (_statementBrackets.TryGetValue(context.Sequence.Value, out var bracket))
            {
                return bracket;
            }
        }

        throw new DatabaseException(
            $"Transaction {context.Sequence} has no statement bracket applying on this database.");
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
    internal async ValueTask<ITransactionContext> BeginAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
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
    internal async ValueTask CommitAsync(ITransactionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _manager.CommitAsync(context, cancellationToken).ConfigureAwait(false);
            _versionStore.OnCommitted(context.Sequence);
        }
        finally
        {
            Untrack(context.Sequence.Value);
        }
    }

    /// <summary>
    /// Rolls the transaction back through the manager: the version store's
    /// ledger physically undoes the writer's stamps (created versions deleted,
    /// tombstones cleared), the abort record is appended, and locks release.
    /// </summary>
    /// <param name="context">The transaction to roll back.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    internal async ValueTask RollbackAsync(ITransactionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _manager.RollbackAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Untrack(context.Sequence.Value);
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
    internal ValueTask<T> ApplyStatementAsync<T>(
        ITransactionContext context,
        Func<IStorageTransaction, T> apply,
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
    internal async ValueTask<T> ApplyStatementAsync<T>(
        ITransactionContext context,
        Func<IStorageTransaction, ValueTask<T>> apply,
        bool durable = false,
        CancellationToken cancellationToken = default)
    {
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
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
                    bracket.Rollback();
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
            _applyGate.Release();
        }
    }

    /// <summary>
    /// Runs the first half of open-time transaction recovery: classifies every
    /// sequence in the recovered journal (<see cref="TransactionRecovery.Analyze"/>),
    /// scrubs every unproven writer's stamps out of the record space (the
    /// open-time form of <see cref="IVersionStore.PurgeWriterAsync"/> — one pass
    /// instead of one scan per writer, because the in-memory ledger died with
    /// the process), seeds the prunable set with surviving committed tombstones,
    /// and anchors the prune bound. The caller scrubs any structures of its own
    /// (secondary indexes) with the returned plan, then calls
    /// <see cref="CompleteRecovery"/> — the checkpoint must come last because
    /// the truncation destroys the lifecycle records classification reads.
    /// </summary>
    /// <returns>The recovery classification, for the caller's own scrub passes.</returns>
    internal TransactionRecoveryPlan AnalyzeAndScrub()
    {
        var plan = TransactionRecovery.Analyze(_storage.WriteAheadJournal);

        _versionStore.ScrubRecovered(plan.Aborted);

        // Anchor the prune bound above every pre-restart stamp: the fresh
        // manager has assigned nothing yet, so its idle oldest-active bound
        // trails the storage's sequence namespace — a reserved sequence is a
        // proven ceiling over every stamp the record space can carry.
        _recoveredSequenceFloor = new TransactionSequence((ulong)_storage.ReserveTransactionSequence());

        return plan;
    }

    /// <summary>
    /// Completes open-time recovery: starts the journal clean (the deferred
    /// open-time checkpoint — see <c>ISqlStorageStrategy.OpenStorage</c>). No
    /// logical transactions exist yet, so the active list is empty.
    /// </summary>
    internal void CompleteRecovery()
    {
        _storage.Checkpoint();
    }

    /// <summary>
    /// Checkpoints the data storage while logical transactions may be in flight:
    /// the checkpoint record carries the sequences of every transaction whose
    /// lifecycle records the truncation is about to destroy, captured atomically
    /// with the truncation (no lifecycle record can land in between), so
    /// recovery classification stays sound.
    /// </summary>
    /// <exception cref="StorageTransactionException">A storage-level bracket is still active.</exception>
    internal void Checkpoint() => _log.CheckpointUnderGate(_storage);

    /// <summary>
    /// Runs one maintenance pass for the version-purge worker: retries any
    /// aborted writer whose undo previously failed, then prunes versions below
    /// the safe bound — the minimum snapshot floor of every open transaction
    /// (a long-running snapshot pins its view), or the manager's oldest-active
    /// bound when idle.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>The number of versions physically reclaimed or undone.</returns>
    internal long RunVersionPurgePass(CancellationToken cancellationToken)
    {
        long total = 0;

        foreach (ulong writer in _versionStore.PendingAbortedPurges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += _versionStore.PurgeWriterAsync(new TransactionSequence(writer), cancellationToken)
                .AsTask().GetAwaiter().GetResult();
        }

        cancellationToken.ThrowIfCancellationRequested();

        total += _versionStore.PruneAsync(GetSafePruneBound(), cancellationToken)
            .AsTask().GetAwaiter().GetResult();

        return total;
    }

    /// <summary>
    /// Computes the prune bound no live or future snapshot can see below: the
    /// minimum <see cref="TransactionSnapshot.Minimum"/> across open
    /// transactions, or <see cref="ITransactionManager.OldestActive"/> when
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
    /// before the storage closes.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync().ConfigureAwait(false);

        lock (_sync)
        {
            _statementBrackets.Clear();
            _openContexts.Clear();
        }
    }

    private void Untrack(ulong sequence)
    {
        lock (_sync)
        {
            _statementBrackets.Remove(sequence);
            _openContexts.Remove(sequence);
        }
    }

    /// <summary>
    /// The manager's journal-bound transaction log, gated so lifecycle appends
    /// and checkpoint truncation are mutually exclusive: the checkpoint captures
    /// the active-sequence list and truncates under the same gate no append can
    /// interleave with, which is what makes recovery classification sound across
    /// truncation. Durability: commit appends the record under the gate and
    /// flushes outside it — if a checkpoint truncated past the record first, the
    /// checkpoint's own durable flush already covered the outcome.
    /// </summary>
    private sealed class GatedJournalLog : ITransactionLog
    {
        private readonly SqlTransactionCoordinator _coordinator;
        private readonly HashSet<long> _activeSequences = new();
        private readonly object _gate = new();

        internal GatedJournalLog(SqlTransactionCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        public ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                _coordinator._storage.WriteAheadJournal.AppendBegin((long)sequence.Value);
                _activeSequences.Add((long)sequence.Value);
            }

            return default;
        }

        public ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long lsn;
            lock (_gate)
            {
                lsn = _coordinator._storage.WriteAheadJournal.AppendCommit((long)sequence.Value);
                _activeSequences.Remove((long)sequence.Value);
            }

            // Durability outside the gate: if a checkpoint truncated past the
            // record it already flushed everything durably, and EnsureDurable on
            // an already-durable LSN is a no-op. By journal ordering this flush
            // also covers every statement bracket the transaction committed
            // non-durably.
            _coordinator._storage.WriteAheadJournal.EnsureDurable(lsn);
            return default;
        }

        public ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                _coordinator._storage.WriteAheadJournal.AppendRollback((long)sequence.Value);
                _activeSequences.Remove((long)sequence.Value);
            }

            return default;
        }

        /// <summary>
        /// Checkpoints the storage with the active-sequence list captured under
        /// the append gate, so no lifecycle record can land between the capture
        /// and the truncation.
        /// </summary>
        internal void CheckpointUnderGate(SqlStorage storage)
        {
            lock (_gate)
            {
                Span<long> actives = _activeSequences.Count <= 64
                    ? stackalloc long[_activeSequences.Count]
                    : new long[_activeSequences.Count];

                int index = 0;
                foreach (long sequence in _activeSequences)
                {
                    actives[index++] = sequence;
                }

                storage.Checkpoint(actives);
            }
        }
    }
}
