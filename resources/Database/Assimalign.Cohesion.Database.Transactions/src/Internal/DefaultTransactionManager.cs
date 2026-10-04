using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

/// <summary>
/// Default transaction manager: assigns sequences, maintains the active-transaction
/// table snapshots are captured from, and drives the write-ahead rule through the
/// transaction log. Locks release as a set at completion; aborted writers are purged
/// from the version store so snapshots never consult them.
/// </summary>
internal sealed class DefaultTransactionManager : ITransactionManager
{
    private readonly ITransactionLog _log;
    private readonly ILockManager _lockManager;
    private readonly IVersionStore _versionStore;
    private readonly Func<TransactionSequence>? _sequenceAllocator;
    private readonly Dictionary<ulong, DefaultTransactionContext> _active = new();
    private readonly object _sync = new();
    private ulong _lastSequence;
    private bool _disposed;

    internal DefaultTransactionManager(
        ITransactionLog log,
        ILockManager lockManager,
        IVersionStore versionStore,
        Func<TransactionSequence>? sequenceAllocator = null)
    {
        _log = log;
        _lockManager = lockManager;
        _versionStore = versionStore;
        _sequenceAllocator = sequenceAllocator;
    }

    /// <inheritdoc />
    public TransactionSequence OldestActive
    {
        get
        {
            lock (_sync)
            {
                if (_active.Count == 0)
                {
                    return new TransactionSequence(_lastSequence + 1);
                }

                ulong oldest = ulong.MaxValue;
                foreach (ulong sequence in _active.Keys)
                {
                    if (sequence < oldest)
                    {
                        oldest = sequence;
                    }
                }

                return new TransactionSequence(oldest);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<ITransactionContext> BeginAsync(
        IsolationLevel isolationLevel = IsolationLevel.Snapshot,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        DefaultTransactionContext context;

        lock (_sync)
        {
            // Sequence assignment and active-table insertion are atomic under the
            // begin lock: a snapshot captured by any other transaction either sees
            // this sequence in the active set or was taken before it existed —
            // there is no window in which an in-flight writer reads as committed.
            // An external allocator (an engine sharing the storage sequence space)
            // is invoked inside the same lock for the same reason; sequences it
            // hands out to non-manager consumers never stamp row versions, so the
            // snapshot maximum below stays a correct visibility bound.
            ulong sequence = _sequenceAllocator is null
                ? ++_lastSequence
                : _sequenceAllocator().Value;

            if (sequence > _lastSequence)
            {
                _lastSequence = sequence;
            }

            var snapshot = CaptureSnapshotLocked(new TransactionSequence(sequence));
            context = new DefaultTransactionContext(
                this, TransactionId.NewId(), new TransactionSequence(sequence), isolationLevel, snapshot);
            _active[sequence] = context;
        }

        try
        {
            await _log.AppendBeginAsync(context.Sequence, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_sync)
            {
                _active.Remove(context.Sequence.Value);
            }

            throw;
        }

        return context;
    }

    /// <inheritdoc />
    public async ValueTask CommitAsync(ITransactionContext context, CancellationToken cancellationToken = default)
    {
        var owned = Validate(context);

        // No statement applies once the end begins; one already applying finishes first,
        // so the commit record follows every bracket stamped with the sequence.
        await owned.BeginEndAsync().ConfigureAwait(false);

        try
        {
            // The write-ahead rule: the log returns only once the commit record is
            // durable. Only then does the transaction leave the active table — no
            // snapshot can observe it as committed before its record is on stable
            // storage.
            await _log.AppendCommitAsync(owned.Sequence, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await AbortAsync(owned).ConfigureAwait(false);
            throw new TransactionAbortedException(
                $"Transaction {owned.Sequence} aborted: the commit record could not be made durable.", exception);
        }

        lock (_sync)
        {
            _active.Remove(owned.Sequence.Value);
        }

        // The state changes before the locks release: a request granted after the
        // release then sees an ended owner and must give the grant back (the engines'
        // post-grant check), and one granted before it is released here.
        owned.State = TransactionState.Committed;
        _lockManager.ReleaseAll(owned.Sequence);
    }

    /// <inheritdoc />
    public async ValueTask RollbackAsync(ITransactionContext context, CancellationToken cancellationToken = default)
    {
        var owned = Validate(context);

        // The undo must see the writer's complete ledger: no statement applies once the
        // end begins, and one already applying records its bracket before the purge runs.
        // Without this, a bracket landing after the purge stays stamped with a sequence
        // every snapshot then reads as committed.
        await owned.BeginEndAsync().ConfigureAwait(false);
        await _versionStore.PurgeWriterAsync(owned.Sequence, cancellationToken).ConfigureAwait(false);
        await _log.AppendAbortAsync(owned.Sequence, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _active.Remove(owned.Sequence.Value);
        }

        owned.State = TransactionState.RolledBack;
        _lockManager.ReleaseAll(owned.Sequence);
    }

    /// <summary>
    /// Admits one statement apply for the context's transaction, which must still be
    /// in the active table and not ending. Every admitted apply is paired with
    /// <see cref="DefaultTransactionContext.ExitApply"/>.
    /// </summary>
    /// <param name="context">The context the statement runs under, or a statement wrapper sharing its sequence.</param>
    /// <returns>The manager's context for the transaction.</returns>
    /// <exception cref="TransactionAbortedException">The transaction has ended or its end has begun.</exception>
    internal DefaultTransactionContext EnterApply(ITransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        DefaultTransactionContext? owned;

        // By sequence, not by reference: engines hand statement wrappers (a
        // read-committed statement's fixed snapshot) that share the sequence.
        lock (_sync)
        {
            _active.TryGetValue(context.Sequence.Value, out owned);
        }

        if (owned is null || !owned.TryEnterApply())
        {
            throw new TransactionAbortedException(
                $"Transaction {context.Sequence} ended while its statement was running; the statement was not applied.");
        }

        return owned;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<DefaultTransactionContext> remaining;
        lock (_sync)
        {
            remaining = _active.Values.ToList();
        }

        foreach (var context in remaining)
        {
            if (context.State == TransactionState.Active)
            {
                await AbortAsync(context).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Captures a fresh snapshot for the specified owner from the current active table.
    /// </summary>
    internal TransactionSnapshot CaptureSnapshot(TransactionSequence owner)
    {
        lock (_sync)
        {
            return CaptureSnapshotLocked(owner);
        }
    }

    private TransactionSnapshot CaptureSnapshotLocked(TransactionSequence owner)
    {
        ulong next = _lastSequence + 1;
        ulong minimum = next;
        var active = new List<TransactionSequence>(_active.Count);

        foreach (ulong sequence in _active.Keys)
        {
            active.Add(new TransactionSequence(sequence));

            if (sequence < minimum)
            {
                minimum = sequence;
            }
        }

        // The owner itself is in flight at capture time.
        if (owner.Value != 0 && owner.Value < minimum)
        {
            minimum = owner.Value;
        }

        return new TransactionSnapshot(owner, new TransactionSequence(minimum), new TransactionSequence(next), active);
    }

    private async ValueTask AbortAsync(DefaultTransactionContext context)
    {
        await context.BeginEndAsync().ConfigureAwait(false);
        await _versionStore.PurgeWriterAsync(context.Sequence).ConfigureAwait(false);

        try
        {
            await _log.AppendAbortAsync(context.Sequence).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The abort record is advisory: recovery treats any sequence without a
            // durable commit record as aborted, so a failed append changes nothing.
        }

        lock (_sync)
        {
            _active.Remove(context.Sequence.Value);
        }

        context.State = TransactionState.Faulted;
        _lockManager.ReleaseAll(context.Sequence);
    }

    private DefaultTransactionContext Validate(ITransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (context is not DefaultTransactionContext owned || !ReferenceEquals(owned.Manager, this))
        {
            throw new TransactionAbortedException("The transaction context was not created by this manager.");
        }

        if (owned.State != TransactionState.Active)
        {
            throw new TransactionAbortedException(
                $"Transaction {owned.Sequence} is not active (state: {owned.State}).");
        }

        return owned;
    }
}
