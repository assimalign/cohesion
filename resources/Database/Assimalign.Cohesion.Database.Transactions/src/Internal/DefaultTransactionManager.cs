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
        var owned = ClaimEnd(context);

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

        _lockManager.ReleaseAll(owned.Sequence);
        owned.State = TransactionState.Committed;
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

        await EndAbortedAsync(owned, TransactionState.RolledBack).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Aborts every transaction still active, then retries every deferred undo once.
    /// An undo that still fails is rethrown after everything else is done: its writer's
    /// versions remain in the record space without a commit record, which only the
    /// next open's recovery scrub can remove, so the owner must not treat the close as
    /// clean.
    /// </remarks>
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
            // A context whose commit or rollback is running is ended by that call.
            if (context.State == TransactionState.Active && context.TryClaimEnd())
            {
                await EndAbortedAsync(context, TransactionState.Faulted).ConfigureAwait(false);
            }
        }

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
            // (xact.c:2873).
            lock (_sync)
            {
                _deferredUndo.Add(sequence.Value);
            }

            context.State = outcome;
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
    /// Validates a context and claims its end for the caller.
    /// </summary>
    private DefaultTransactionContext ClaimEnd(ITransactionContext context)
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

        if (!owned.TryClaimEnd())
        {
            throw new TransactionAbortedException(
                $"Transaction {owned.Sequence} is already ending: its commit or rollback is running.");
        }

        return owned;
    }
}
