using System;
using System.Diagnostics;
using System.Threading;

namespace Assimalign.Cohesion.Database.Storage.Internal;

/// <summary>
/// Coordinates grouped commit durability between committers and the write-ahead
/// flush worker: committers register the LSN they need durable and wait; the flush
/// worker performs one durable flush that covers every pending commit and wakes all
/// of them, so concurrent commits share a single fsync.
/// </summary>
/// <remarks>
/// Correctness never depends on the worker: a committer that is not woken within its
/// bounded window flushes the journal inline itself (self-help), so a commit is
/// <em>never</em> acknowledged before its records are durable — a missing or stalled
/// worker only costs latency, never durability.
/// </remarks>
internal sealed class StorageGroupCommitGate
{
    private readonly object _syncRoot = new();
    private long _requestedLsn;
    private long _durableLsn;

    /// <summary>
    /// Invoked (outside the gate lock) when a committer registers a new pending
    /// commit, so an engine-level flush worker can be woken. Set by the owning
    /// storage's composition.
    /// </summary>
    internal Action? CommitPending;

    /// <summary>
    /// Blocks until the journal is durable at least up to <paramref name="lsn"/>:
    /// first waiting up to <paramref name="window"/> for the flush worker's group
    /// flush, then flushing inline as self-help if the worker did not respond.
    /// </summary>
    internal void AwaitDurable(long lsn, TimeSpan window, IStorageJournal journal)
    {
        bool signal = false;

        lock (_syncRoot)
        {
            if (_durableLsn >= lsn)
            {
                return;
            }

            if (lsn > _requestedLsn)
            {
                _requestedLsn = lsn;
            }

            signal = true;
        }

        if (signal)
        {
            CommitPending?.Invoke();
        }

        long start = Stopwatch.GetTimestamp();

        lock (_syncRoot)
        {
            while (_durableLsn < lsn)
            {
                TimeSpan remaining = window - Stopwatch.GetElapsedTime(start);

                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_syncRoot, remaining))
                {
                    break;
                }
            }

            if (_durableLsn >= lsn)
            {
                return;
            }
        }

        // Self-help: the worker did not flush within the window. Flush inline (a
        // no-op if another self-helper got there first) and publish so any other
        // waiter covered by this flush wakes too.
        journal.EnsureDurable(lsn);
        PublishDurable(journal.DurableLsn);
    }

    /// <summary>
    /// Performs one group flush pass on behalf of the flush worker: makes the journal
    /// durable up to the highest pending commit LSN and wakes every covered waiter.
    /// </summary>
    /// <returns>True when a flush was performed; false when nothing was pending.</returns>
    internal bool FlushPending(IStorageJournal journal)
    {
        long target;

        lock (_syncRoot)
        {
            target = _requestedLsn;

            if (target <= _durableLsn)
            {
                return false;
            }
        }

        journal.EnsureDurable(target);
        PublishDurable(journal.DurableLsn);
        return true;
    }

    /// <summary>
    /// Publishes a durable LSN and wakes every waiter it covers.
    /// </summary>
    internal void PublishDurable(long lsn)
    {
        lock (_syncRoot)
        {
            if (lsn > _durableLsn)
            {
                _durableLsn = lsn;
                Monitor.PulseAll(_syncRoot);
            }
        }
    }
}
