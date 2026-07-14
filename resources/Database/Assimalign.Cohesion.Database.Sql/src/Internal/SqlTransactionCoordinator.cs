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
/// <see cref="IVersionStore"/> over the database's data storage, with the
/// manager's transaction log bound to the storage's write-ahead journal. The
/// coordinator owns the pairing between logical transaction contexts and their
/// physical storage brackets (<see cref="IStorageTransactionSource"/>), the
/// sequence-space unification (the manager allocates from the storage's
/// counter, so the journal carries one sequence namespace), and the
/// checkpoint interlock that keeps truncation classification-safe while
/// logical transactions are in flight.
/// </summary>
/// <remarks>
/// Scope decision: one coordinator (and therefore one manager, lock manager,
/// and version store) <b>per database</b>, not per engine — the journal-bound
/// transaction log, recovery analysis, and the <c>OldestActive</c> prune bound
/// are all properties of one database's journal and record space, and a
/// per-engine manager would couple unrelated databases' snapshot horizons.
/// </remarks>
internal sealed class SqlTransactionCoordinator : IStorageTransactionSource, IAsyncDisposable
{
    private readonly SqlStorage _storage;
    private readonly ITransactionManager _manager;
    private readonly ILockManager _lockManager;
    private readonly IVersionStore _versionStore;
    private readonly GatedJournalLog _log;
    private readonly Dictionary<ulong, PairedTransaction> _paired = new();
    private readonly object _pairingLock = new();

    internal SqlTransactionCoordinator(SqlStorage storage)
    {
        _storage = storage;

        // Fully qualified: the coordinator's LockManager/VersionStore properties
        // shadow the factory class names inside this scope.
        _lockManager = Transactions.LockManager.Create();
        _versionStore = Transactions.VersionStore.CreateInMemory();
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
    /// Gets the version store aborted writers are purged through.
    /// </summary>
    internal IVersionStore VersionStore => _versionStore;

    /// <summary>
    /// Gets the number of logical transactions currently paired with a storage
    /// bracket (test observability).
    /// </summary>
    internal int PairedTransactionCount
    {
        get
        {
            lock (_pairingLock)
            {
                return _paired.Count;
            }
        }
    }

    /// <inheritdoc />
    public IStorageTransaction GetStorageTransaction(ITransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_pairingLock)
        {
            if (_paired.TryGetValue(context.Sequence.Value, out var paired))
            {
                return paired.StorageTransaction;
            }
        }

        throw new DatabaseException(
            $"Transaction {context.Sequence} has no paired storage transaction on this database.");
    }

    /// <summary>
    /// Begins a logical transaction paired with its physical storage bracket:
    /// the manager assigns the sequence from the storage's counter (one
    /// namespace), appends the begin record through the journal-bound log, and
    /// the storage bracket adopts the same sequence — page images and the
    /// commit record it writes prove the logical transaction at recovery.
    /// </summary>
    /// <param name="isolationLevel">The isolation level the transaction runs under.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The paired transaction context.</returns>
    internal async ValueTask<ITransactionContext> BeginAsync(IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        var context = await _manager.BeginAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

        try
        {
            var bracket = _storage.BeginTransaction((long)context.Sequence.Value);

            lock (_pairingLock)
            {
                _paired[context.Sequence.Value] = new PairedTransaction(context, bracket);
            }

            return context;
        }
        catch
        {
            await _manager.RollbackAsync(context, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Commits the paired transaction through the manager. The journal-bound log
    /// resolves the paired storage bracket and commits it — after images, the
    /// shared-sequence commit record, and the durability await all ride the
    /// storage commit — so commit acknowledges only after durability, while the
    /// transaction is still in the manager's active table.
    /// </summary>
    /// <param name="context">The transaction to commit.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="TransactionAbortedException">The transaction was aborted instead of committed.</exception>
    internal async ValueTask CommitAsync(ITransactionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _manager.CommitAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Unpair(context.Sequence.Value);
        }
    }

    /// <summary>
    /// Rolls the paired transaction back through the manager: versions purged,
    /// the storage bracket physically restored (before images), locks released.
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
            Unpair(context.Sequence.Value);
        }
    }

    /// <summary>
    /// Runs open-time transaction recovery: classifies every sequence in the
    /// recovered journal (<see cref="TransactionRecovery.Analyze"/>), drives
    /// <see cref="IVersionStore.PurgeWriterAsync"/> for every sequence the
    /// journal cannot prove committed, and then checkpoints the storage so the
    /// journal starts clean — in that order, because the truncation destroys the
    /// lifecycle records classification reads.
    /// </summary>
    internal void Recover()
    {
        var plan = TransactionRecovery.Analyze(_storage.WriteAheadJournal);

        foreach (var sequence in plan.Aborted)
        {
            // Synchronous by design: the in-process store implementations
            // complete synchronously, and instance open is a synchronous path.
            _versionStore.PurgeWriterAsync(sequence).AsTask().GetAwaiter().GetResult();
        }

        // Analysis is done; start the journal clean (the deferred open-time
        // checkpoint — see ISqlStorageStrategy.OpenStorage). No logical
        // transactions exist yet, so the active list is empty.
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
    /// Disposes the coordinator: the manager aborts every still-active
    /// transaction (purging versions and rolling back the paired brackets
    /// through the journal-bound log) before the storage closes.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync().ConfigureAwait(false);

        lock (_pairingLock)
        {
            _paired.Clear();
        }
    }

    private void Unpair(ulong sequence)
    {
        lock (_pairingLock)
        {
            _paired.Remove(sequence);
        }
    }

    private PairedTransaction? TryGetPaired(ulong sequence)
    {
        lock (_pairingLock)
        {
            return _paired.TryGetValue(sequence, out var paired) ? paired : null;
        }
    }

    private sealed record PairedTransaction(ITransactionContext Context, IStorageTransaction StorageTransaction);

    /// <summary>
    /// The manager's journal-bound transaction log, gated so lifecycle appends
    /// and checkpoint truncation are mutually exclusive: the checkpoint captures
    /// the active-sequence list and truncates under the same gate no append can
    /// interleave with, which is what makes recovery classification sound across
    /// truncation. Commit and abort resolve the paired storage bracket when one
    /// is active — the bracket's own commit/rollback writes the shared-sequence
    /// record (after images first, then the commit record, then the durability
    /// await, honoring the engine's grouped/synchronous durability policy).
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

            var paired = _coordinator.TryGetPaired(sequence.Value);

            if (paired is not null && paired.StorageTransaction.IsActive)
            {
                // The bracket's commit IS the transaction's commit record: after
                // images, the commit record under the shared sequence, and the
                // durability await (synchronous fsync or the grouped gate). The
                // append gate is not held across it — the bracket's active count
                // already excludes checkpoints for its whole lifetime.
                paired.StorageTransaction.Commit();

                lock (_gate)
                {
                    _activeSequences.Remove((long)sequence.Value);
                }

                return default;
            }

            long lsn;
            lock (_gate)
            {
                lsn = _coordinator._storage.WriteAheadJournal.AppendCommit((long)sequence.Value);
                _activeSequences.Remove((long)sequence.Value);
            }

            // Durability outside the gate: if a checkpoint truncated past the
            // record it already flushed everything durably, and EnsureDurable on
            // an already-durable LSN is a no-op.
            _coordinator._storage.WriteAheadJournal.EnsureDurable(lsn);
            return default;
        }

        public ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var paired = _coordinator.TryGetPaired(sequence.Value);

            if (paired is not null && paired.StorageTransaction.IsActive)
            {
                // Physical revert: before images restored, rollback record under
                // the shared sequence.
                paired.StorageTransaction.Rollback();

                lock (_gate)
                {
                    _activeSequences.Remove((long)sequence.Value);
                }

                return default;
            }

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
