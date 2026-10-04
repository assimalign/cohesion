using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

/// <summary>
/// Default transaction manager: assigns sequences, maintains the active-transaction
/// table snapshots are captured from, and drives the write-ahead rule through the
/// transaction log. Locks release as a set at completion; aborted writers are purged
/// from the version store so snapshots never consult them.
/// </summary>
/// <remarks>
/// <para>
/// <b>A started rollback always ends its transaction (#1226).</b> Rollback observes
/// the caller's token only before it claims the context's end. From the claim on,
/// nothing the caller does and nothing that fails can leave the context active: the
/// undo and the abort record run with no token, a lost abort record is ignored, and
/// the context ends as <see cref="TransactionState.RolledBack"/> (an abort that a
/// failed commit forces ends it as <see cref="TransactionState.Faulted"/>).
/// </para>
/// <para>
/// What the end releases depends on the undo. When the writer's versions are undone,
/// the abort record is appended, the writer leaves the active table and its locks are
/// released, in that order. When the undo itself fails, the writer's versions are
/// still in the record space, so the writer stays in the active table (every snapshot
/// keeps treating it as in flight, which hides those versions) and keeps its locks,
/// and the abort record is not written (the journal keeps classifying the writer as
/// unproven). That deferred undo belongs to the manager, not to the caller:
/// <see cref="RetryDeferredUndoAsync"/> (the coordinator's version-purge pass) and
/// <see cref="DisposeAsync"/> retry it and release the writer once it completes.
/// </para>
/// <para>
/// A writer's locks are released only when it leaves the active table, so
/// <see cref="IsTracked"/> tells an owner of the lock manager whether a release is
/// still the manager's to make. Its queued lock requests, by contrast, fail the
/// moment its transaction ends, deferred undo or not: the release fails them at
/// every other end (#1225), and a deferred end fails them without releasing a grant.
/// </para>
/// <para>
/// <b>Every end waits for the statement applies already running (#1225).</b> The
/// claim of the end also closes the context's apply admission
/// (<see cref="EnterApply"/>), and the end then waits, without a token, for the
/// applies admitted before it, so a commit record follows every bracket stamped with
/// the sequence and an undo sees the writer's complete ledger. The state of an ended
/// transaction changes before its locks release, so an engine whose request is
/// granted after the release sees the end and gives the grant back.
/// </para>
/// </remarks>
internal sealed class DefaultTransactionManager : ITransactionManager
{
    private readonly ITransactionLog _log;
    private readonly ILockManager _lockManager;
    private readonly IVersionStore _versionStore;
    private readonly Func<TransactionSequence>? _sequenceAllocator;
    private readonly Dictionary<ulong, DefaultTransactionContext> _active = new();

    // Writers whose transaction ended but whose undo did not complete. Each one is still
    // in _active and still holds its locks.
    private readonly HashSet<ulong> _deferredUndo = new();

    // Serializes the retries of deferred undo, so two retries never undo one writer at once.
    private readonly SemaphoreSlim _undoRetryGate = new(1, 1);
    private readonly object _sync = new();
    private ulong _lastSequence;
    private bool _disposed;

    // Commits and rollbacks that claimed their context's end and have not returned yet,
    // guarded by _sync. Disposal waits for them, so the manager never reports a close
    // while an end still runs against the storage its owner is about to close.
    private int _runningEnds;
    private TaskCompletionSource? _endsDrained;

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
            // Checked again under this lock, which disposal holds while it sets its flag and
            // copies the active table, so no transaction begins after that copy and escapes
            // disposal's abort.
            ObjectDisposedException.ThrowIf(_disposed, this);

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
        var owned = ClaimEnd(context);

        try
        {
            // No statement applies once the end is claimed; one already applying finishes
            // first, so the commit record follows every bracket stamped with the sequence.
            await owned.WaitForAppliesAsync().ConfigureAwait(false);

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
                await EndAbortedAsync(owned, TransactionState.Faulted).ConfigureAwait(false);
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
        finally
        {
            ExitEnd();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The token is observed only before the rollback starts: a token canceled by
    /// then throws <see cref="OperationCanceledException"/> and leaves the
    /// transaction active and untouched. Once started, the rollback runs to
    /// completion and throws nothing — a canceled token, a failed abort record and a
    /// failed undo all still end the transaction (see the class remarks).
    /// </remarks>
    public async ValueTask RollbackAsync(ITransactionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owned = ClaimEnd(context);

        try
        {
            await EndAbortedAsync(owned, TransactionState.RolledBack).ConfigureAwait(false);
        }
        finally
        {
            ExitEnd();
        }
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

        // A writer whose undo is deferred is still in the active table, but its end was
        // claimed, so it is refused here like any other ended transaction.
        if (owned is null || !owned.TryEnterApply())
        {
            throw new TransactionAbortedException(
                $"Transaction {context.Sequence} ended while its statement was running; the statement was not applied.");
        }

        return owned;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Aborts every transaction still active, waits for every commit or rollback that
    /// had already started, then retries every deferred undo once. An undo that still
    /// fails is rethrown after everything else is done: its writer's versions remain in
    /// the record space without a commit record, and its writer stays in the active
    /// table (<see cref="GetDeferredUndoWriters"/>), so the owner must not treat the
    /// close as clean. Only the next open's recovery can remove those versions, and only
    /// if the journal still classifies the writer then; the coordinator keeps it so.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        List<DefaultTransactionContext> remaining;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            // Every end claims under this lock (ClaimEnd), so each one either claimed before
            // this point, and is counted in _runningEnds, or sees the flag and is refused.
            _disposed = true;
            remaining = _active.Values.ToList();
        }

        foreach (var context in remaining)
        {
            // A context whose commit or rollback claimed its end first is ended by that
            // call, which the wait below covers; a deferred writer's end was claimed too.
            if (context.TryClaimEnd())
            {
                await EndAbortedAsync(context, TransactionState.Faulted).ConfigureAwait(false);
            }
        }

        Task drained;
        lock (_sync)
        {
            drained = _runningEnds == 0
                ? Task.CompletedTask
                : (_endsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await drained.ConfigureAwait(false);

        await _undoRetryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RetryDeferredUndoCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _undoRetryGate.Release();
        }
    }

    /// <summary>
    /// Retries the undo of every writer whose transaction ended while its undo failed.
    /// Each writer whose undo now completes gets its abort record, leaves the active
    /// table and releases its locks; a writer whose undo fails again stays deferred.
    /// </summary>
    /// <param name="cancellationToken">
    /// Observed between writers only: a writer's undo, once started, runs to completion.
    /// </param>
    /// <returns>The number of versions and index entries the completed undos changed.</returns>
    /// <exception cref="ObjectDisposedException">The manager was disposed.</exception>
    /// <remarks>
    /// Every deferred writer is attempted. When any of them fails again, the first
    /// failure is rethrown after the pass; the others stay queued for the next pass.
    /// </remarks>
    internal async ValueTask<long> RetryDeferredUndoAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _undoRetryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RetryDeferredUndoCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _undoRetryGate.Release();
        }
    }

    /// <summary>
    /// Gets whether the specified writer's transaction ended with its undo deferred.
    /// </summary>
    /// <param name="writer">The writer's sequence.</param>
    /// <returns>True while the manager owns the writer's undo.</returns>
    internal bool IsUndoDeferred(ulong writer)
    {
        lock (_sync)
        {
            return _deferredUndo.Contains(writer);
        }
    }

    /// <summary>
    /// Gets the writers whose transaction ended while their undo did not complete.
    /// </summary>
    /// <returns>The sequences of the writers the manager still owns an undo for.</returns>
    internal ulong[] GetDeferredUndoWriters()
    {
        lock (_sync)
        {
            return [.. _deferredUndo];
        }
    }

    /// <summary>
    /// Gets whether the specified transaction is still in the active table: running, ending,
    /// or ended with its undo deferred.
    /// </summary>
    /// <param name="sequence">The transaction's sequence.</param>
    /// <returns>
    /// True while the manager still owes the transaction's lock release. The manager
    /// releases every lock of a transaction, including one granted after its end, at the
    /// moment the transaction leaves the active table.
    /// </returns>
    internal bool IsTracked(ulong sequence)
    {
        lock (_sync)
        {
            return _active.ContainsKey(sequence);
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

    /// <summary>
    /// Ends a claimed context as aborted. Throws nothing and observes no token: this is
    /// the part of a rollback that, once started, always completes.
    /// </summary>
    private async ValueTask EndAbortedAsync(DefaultTransactionContext context, TransactionState outcome)
    {
        var sequence = context.Sequence;

        // The undo must see the writer's complete ledger: no statement applies once the
        // end is claimed, and one already applying records its bracket before the purge
        // runs. Without this, a bracket landing after the purge stays stamped with a
        // sequence every snapshot then reads as committed (#1225). Idempotent for a
        // commit that already waited.
        await context.WaitForAppliesAsync().ConfigureAwait(false);

        try
        {
            await _versionStore.PurgeWriterAsync(sequence, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The versions are still in the record space. Releasing the writer now would
            // let every new snapshot read them as committed and let the next lock holder
            // build on them, so the writer stays in the active table with its locks, and
            // the manager owns the retry, which reports the failure if it recurs.
            // PostgreSQL likewise holds regular locks "till we finish aborting"
            // (xact.c:2873). The context ends before the writer is published to the
            // retry, so no retry can release a writer whose context still reads Active.
            context.State = outcome;

            lock (_sync)
            {
                _deferredUndo.Add(sequence.Value);
            }

            // The transaction has ended even though its grants stay held: a request of
            // it still queued fails now, as the release at any other end fails it,
            // instead of waiting for a grant the ended transaction could only give back.
            AbandonPendingRequests(sequence);
            return;
        }

        context.State = outcome;
        await ReleaseUndoneAsync(sequence).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases a writer whose undo completed: appends its abort record, removes it
    /// from the active table and releases its locks.
    /// </summary>
    private async ValueTask ReleaseUndoneAsync(TransactionSequence sequence)
    {
        try
        {
            await _log.AppendAbortAsync(sequence, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The abort record is advisory: recovery classifies every sequence without a
            // durable commit record as aborted (TransactionRecovery.Analyze), so a lost
            // abort record changes nothing. PostgreSQL does not even flush its abort
            // record, "since the default assumption after a crash would be that we
            // aborted, anyway" (xact.c:1825-1827).
        }

        lock (_sync)
        {
            _active.Remove(sequence.Value);
        }

        _lockManager.ReleaseAll(sequence);
    }

    /// <summary>
    /// Fails the writer's queued lock requests without releasing its grants: the end of
    /// a transaction whose undo is deferred.
    /// </summary>
    /// <remarks>
    /// Only the default lock manager can split the two halves of
    /// <see cref="ILockManager.ReleaseAll"/>; the public lock-manager contract has no
    /// such member, and none is added for this (owner direction of 2026-10-03). With
    /// another lock manager a deferred writer's queued request stays queued until the
    /// deferred undo completes and the release fails it, or until it is granted and the
    /// engine's post-grant check refuses the ended transaction.
    /// </remarks>
    private void AbandonPendingRequests(TransactionSequence sequence)
    {
        if (_lockManager is DefaultLockManager locks)
        {
            locks.AbandonPending(sequence);
        }
    }

    private async ValueTask<long> RetryDeferredUndoCoreAsync(CancellationToken cancellationToken)
    {
        ulong[] writers;
        lock (_sync)
        {
            writers = [.. _deferredUndo];
        }

        long total = 0;
        Exception? failure = null;

        foreach (ulong writer in writers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = new TransactionSequence(writer);
            long undone;

            try
            {
                undone = await _versionStore.PurgeWriterAsync(sequence, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Still deferred: the writer keeps its place in the active table and its locks.
                failure ??= exception;
                continue;
            }

            lock (_sync)
            {
                _deferredUndo.Remove(writer);
            }

            await ReleaseUndoneAsync(sequence).ConfigureAwait(false);
            total += undone;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return total;
    }

    /// <summary>
    /// Validates a context and claims its end for the caller. Every successful claim is
    /// paired with one <see cref="ExitEnd"/> when the end returns.
    /// </summary>
    private DefaultTransactionContext ClaimEnd(ITransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (context is not DefaultTransactionContext owned || !ReferenceEquals(owned.Manager, this))
        {
            throw new TransactionAbortedException("The transaction context was not created by this manager.");
        }

        lock (_sync)
        {
            // Disposal sets its flag under this lock: an end claimed here is counted before
            // disposal can look, so disposal waits for it.
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (owned.State != TransactionState.Active)
            {
                throw new TransactionAbortedException(
                    $"Transaction {owned.Sequence} is not active (state: {owned.State}).");
            }

            if (!owned.TryClaimEnd())
            {
                throw new TransactionAbortedException(
                    $"Transaction {owned.Sequence} is already ending: its commit or rollback is running.");
            }

            _runningEnds++;
        }

        return owned;
    }

    /// <summary>
    /// Marks a claimed end as returned, and wakes a disposal waiting for the last one.
    /// </summary>
    private void ExitEnd()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (--_runningEnds == 0)
            {
                drained = _endsDrained;
                _endsDrained = null;
            }
        }

        drained?.TrySetResult();
    }
}
