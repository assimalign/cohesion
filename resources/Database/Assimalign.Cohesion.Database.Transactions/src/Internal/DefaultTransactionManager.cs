using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions;

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
    private readonly Dictionary<ulong, DefaultTransactionContext> _active = new();
    private readonly object _sync = new();
    private ulong _lastSequence;
    private bool _disposed;

    internal DefaultTransactionManager(ITransactionLog log, ILockManager lockManager, IVersionStore versionStore)
    {
        _log = log;
        _lockManager = lockManager;
        _versionStore = versionStore;
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
            ulong sequence = ++_lastSequence;
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

        _lockManager.ReleaseAll(owned.Sequence);
        owned.State = TransactionState.Committed;
    }

    /// <inheritdoc />
    public async ValueTask RollbackAsync(ITransactionContext context, CancellationToken cancellationToken = default)
    {
        var owned = Validate(context);

        await _versionStore.PurgeWriterAsync(owned.Sequence, cancellationToken).ConfigureAwait(false);
        await _log.AppendAbortAsync(owned.Sequence, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _active.Remove(owned.Sequence.Value);
        }

        _lockManager.ReleaseAll(owned.Sequence);
        owned.State = TransactionState.RolledBack;
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

        _lockManager.ReleaseAll(context.Sequence);
        context.State = TransactionState.Faulted;
    }

    private DefaultTransactionContext Validate(ITransactionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (context is not DefaultTransactionContext owned)
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
