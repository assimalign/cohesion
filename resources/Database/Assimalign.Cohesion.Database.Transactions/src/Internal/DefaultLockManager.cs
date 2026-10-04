using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Transactions.Internal;

/// <summary>
/// Default lock manager: a lock table keyed by <see cref="LockResource"/> with a
/// mode-compatibility matrix, FIFO waiter wake-up, and wait-for-graph deadlock
/// detection that aborts the requester whose wait would close a cycle.
/// </summary>
internal sealed class DefaultLockManager : ILockManager
{
    // Compatibility matrix indexed [held, requested]:
    // Shared, Update, Exclusive, IntentShared, IntentExclusive.
    private static readonly bool[,] _compatible =
    {
        //               S      U      X      IS     IX
        /* S  */ { true,  true,  false, true,  false },
        /* U  */ { true,  false, false, true,  false },
        /* X  */ { false, false, false, false, false },
        /* IS */ { true,  true,  false, true,  true  },
        /* IX */ { false, false, false, true,  true  },
    };

    private readonly Dictionary<LockResource, LockEntry> _table = new();
    private readonly Dictionary<ulong, HashSet<ulong>> _waitFor = new();
    private readonly object _sync = new();

    /// <inheritdoc />
    public async ValueTask AcquireAsync(
        TransactionSequence owner,
        LockResource resource,
        LockMode mode,
        CancellationToken cancellationToken = default)
    {
        Waiter waiter;

        lock (_sync)
        {
            var entry = GetEntryLocked(resource);

            if (TryGrantLocked(entry, owner.Value, mode))
            {
                return;
            }

            // Record who this request waits for; a cycle means granting can never
            // happen without an abort — the requester is the victim.
            var blockers = CollectBlockersLocked(entry, owner.Value, mode);
            AddWaitEdgesLocked(owner.Value, blockers);

            if (CreatesCycleLocked(owner.Value))
            {
                RemoveWaitEdgesLocked(owner.Value);
                throw new TransactionDeadlockException(
                    $"Transaction {owner} was chosen as the deadlock victim requesting {mode} on {resource}.");
            }

            waiter = new Waiter(owner.Value, mode);
            entry.Waiters.Add(waiter);
        }

        using var registration = cancellationToken.Register(() => CancelWaiter(resource, waiter));

        await waiter.Completion.Task.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TryAcquire(TransactionSequence owner, LockResource resource, LockMode mode)
    {
        lock (_sync)
        {
            return TryGrantLocked(GetEntryLocked(resource), owner.Value, mode);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The owner's own pending requests fail with <see cref="TransactionAbortedException"/>
    /// instead of staying queued: a grant that arrived after the owner ended would hold
    /// the resource for a transaction that can never release it again. Neo4j's lock
    /// client does the same when its transaction is terminated: the wait loop checks the
    /// stopped flag and throws (<c>community/lock/.../forseti/ForsetiClient.java:1081-1085</c>,
    /// checked at <c>:230</c>). A request queued after this call is not seen here, so a
    /// caller that can race an end checks its transaction after the grant and releases it.
    /// </remarks>
    public void ReleaseAll(TransactionSequence owner)
    {
        List<Waiter> granted = new();
        List<(LockResource Resource, Waiter Waiter)>? ended = null;

        lock (_sync)
        {
            RemoveWaitEdgesLocked(owner.Value);

            List<LockResource>? empty = null;

            foreach (var (resource, entry) in _table)
            {
                entry.Granted.Remove(owner.Value);

                for (int i = entry.Waiters.Count - 1; i >= 0; i--)
                {
                    if (entry.Waiters[i].Owner == owner.Value)
                    {
                        (ended ??= new()).Add((resource, entry.Waiters[i]));
                        entry.Waiters.RemoveAt(i);
                    }
                }

                // Wake compatible waiters in FIFO order.
                for (int i = 0; i < entry.Waiters.Count;)
                {
                    var waiter = entry.Waiters[i];

                    if (TryGrantLocked(entry, waiter.Owner, waiter.Mode))
                    {
                        entry.Waiters.RemoveAt(i);
                        RemoveWaitEdgesLocked(waiter.Owner);
                        granted.Add(waiter);
                    }
                    else
                    {
                        i++;
                    }
                }

                if (entry.Granted.Count == 0 && entry.Waiters.Count == 0)
                {
                    (empty ??= new List<LockResource>()).Add(resource);
                }
            }

            if (empty is not null)
            {
                foreach (var resource in empty)
                {
                    _table.Remove(resource);
                }
            }
        }

        foreach (var waiter in granted)
        {
            waiter.Completion.TrySetResult();
        }

        FailEnded(owner, ended);
    }

    /// <summary>
    /// Fails the owner's queued requests with <see cref="TransactionAbortedException"/>,
    /// as <see cref="ReleaseAll"/> does, but keeps every lock the owner holds.
    /// </summary>
    /// <param name="owner">The transaction that ended while its locks stay held.</param>
    /// <remarks>
    /// The end of a rolled-back writer whose undo is deferred (#1226): its transaction
    /// has ended, so a request it still has queued must not wait for a grant, but its
    /// granted locks protect versions the undo has not removed yet and stay until the
    /// manager releases them. Removing a queued request grants nothing to anyone else:
    /// a grant depends only on the modes held, never on the queue ahead of it.
    /// </remarks>
    internal void AbandonPending(TransactionSequence owner)
    {
        List<(LockResource Resource, Waiter Waiter)>? ended = null;

        lock (_sync)
        {
            RemoveWaitEdgesLocked(owner.Value);

            foreach (var (resource, entry) in _table)
            {
                for (int i = entry.Waiters.Count - 1; i >= 0; i--)
                {
                    if (entry.Waiters[i].Owner == owner.Value)
                    {
                        (ended ??= new()).Add((resource, entry.Waiters[i]));
                        entry.Waiters.RemoveAt(i);
                    }
                }
            }
        }

        FailEnded(owner, ended);
    }

    private static void FailEnded(TransactionSequence owner, List<(LockResource Resource, Waiter Waiter)>? ended)
    {
        if (ended is null)
        {
            return;
        }

        foreach (var (resource, waiter) in ended)
        {
            waiter.Completion.TrySetException(new TransactionAbortedException(
                $"Transaction {owner} ended while it waited for {waiter.Mode} on {resource}; the request was not granted."));
        }
    }

    private void CancelWaiter(LockResource resource, Waiter waiter)
    {
        lock (_sync)
        {
            if (_table.TryGetValue(resource, out var entry))
            {
                entry.Waiters.Remove(waiter);
            }

            RemoveWaitEdgesLocked(waiter.Owner);
        }

        waiter.Completion.TrySetCanceled();
    }

    private LockEntry GetEntryLocked(LockResource resource)
    {
        if (!_table.TryGetValue(resource, out var entry))
        {
            entry = new LockEntry();
            _table[resource] = entry;
        }

        return entry;
    }

    /// <summary>
    /// Grants when the requested mode is compatible with every mode held by other
    /// owners (an owner's own grants never block it — upgrades are supported).
    /// </summary>
    private static bool TryGrantLocked(LockEntry entry, ulong owner, LockMode mode)
    {
        foreach (var (holder, heldMode) in entry.Granted)
        {
            if (holder == owner)
            {
                continue;
            }

            if (!_compatible[(int)heldMode, (int)mode])
            {
                return false;
            }
        }

        if (entry.Granted.TryGetValue(owner, out var existing))
        {
            // Keep the strongest mode held.
            if (Strength(mode) > Strength(existing))
            {
                entry.Granted[owner] = mode;
            }
        }
        else
        {
            entry.Granted[owner] = mode;
        }

        return true;
    }

    private static int Strength(LockMode mode) => mode switch
    {
        LockMode.IntentShared => 0,
        LockMode.Shared => 1,
        LockMode.IntentExclusive => 2,
        LockMode.Update => 3,
        LockMode.Exclusive => 4,
        _ => 0,
    };

    private static List<ulong> CollectBlockersLocked(LockEntry entry, ulong owner, LockMode mode)
    {
        var blockers = new List<ulong>();

        foreach (var (holder, heldMode) in entry.Granted)
        {
            if (holder != owner && !_compatible[(int)heldMode, (int)mode])
            {
                blockers.Add(holder);
            }
        }

        return blockers;
    }

    private void AddWaitEdgesLocked(ulong waiter, List<ulong> blockers)
    {
        if (!_waitFor.TryGetValue(waiter, out var edges))
        {
            edges = new HashSet<ulong>();
            _waitFor[waiter] = edges;
        }

        foreach (ulong blocker in blockers)
        {
            edges.Add(blocker);
        }
    }

    private void RemoveWaitEdgesLocked(ulong owner)
    {
        _waitFor.Remove(owner);
    }

    /// <summary>
    /// Depth-first reachability: does any transaction this owner waits for
    /// (transitively) wait for the owner?
    /// </summary>
    private bool CreatesCycleLocked(ulong owner)
    {
        var visited = new HashSet<ulong>();
        var stack = new Stack<ulong>();

        if (_waitFor.TryGetValue(owner, out var direct))
        {
            foreach (ulong blocker in direct)
            {
                stack.Push(blocker);
            }
        }

        while (stack.Count > 0)
        {
            ulong current = stack.Pop();

            if (current == owner)
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            if (_waitFor.TryGetValue(current, out var next))
            {
                foreach (ulong blocker in next)
                {
                    stack.Push(blocker);
                }
            }
        }

        return false;
    }

    private sealed class LockEntry
    {
        public Dictionary<ulong, LockMode> Granted { get; } = new();

        public List<Waiter> Waiters { get; } = new();
    }

    private sealed class Waiter
    {
        public Waiter(ulong owner, LockMode mode)
        {
            Owner = owner;
            Mode = mode;
        }

        public ulong Owner { get; }

        public LockMode Mode { get; }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
