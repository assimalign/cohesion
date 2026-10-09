using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Grants and releases hierarchical locks for write-write conflict control: a lock table keyed
/// by <see cref="LockResource"/> with a mode-compatibility matrix, FIFO waiter wake-up, and
/// wait-for-graph deadlock detection that aborts the requester whose wait would close a cycle.
/// </summary>
/// <remarks>
/// <para>
/// MVCC keeps readers lock-free; the lock manager arbitrates writers. Locks are owned by
/// transaction sequences and are released as a set at commit or rollback. A request whose wait
/// would close a cycle is chosen as the deadlock victim and fails with
/// <see cref="TransactionDeadlockException"/>.
/// </para>
/// <para>
/// <b>The coordinator's engine mode (#1226, #1268).</b> A <see cref="TransactionCoordinator"/>
/// hands its engine the same lock manager its <see cref="TransactionManager"/> uses, in an
/// internal mode it installs once. In that mode <see cref="ReleaseAll"/> releases nothing for a
/// transaction the manager still tracks: the manager releases that transaction's locks itself,
/// through an internal unfiltered release, when the transaction leaves its active table. That is
/// what keeps a rolled-back writer whose undo is deferred holding its locks. A wait in that mode
/// also ends when the database's storage goes offline. A lock manager from <see cref="Create"/>
/// has no such mode and behaves as a plain lock table. The mode replaced a private decorator,
/// which a sealed type cannot have (plan §6.2).
/// </para>
/// </remarks>
public sealed class LockManager
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

    // Owners whose transaction ended while their grants stay held (AbandonPending), until
    // ReleaseAll releases them. Such an owner gets nothing new: see RefuseAbandonedLocked.
    private readonly HashSet<ulong> _abandoned = new();
    private readonly object _sync = new();

    // The coordinator's engine mode, null for a standalone lock manager: whether the transaction
    // manager still owes an owner's release (ReleaseAll then leaves it to the manager), and the
    // source canceled, with its cause kept beside it, once the storage went offline (Abandon).
    private Func<ulong, bool>? _releaseOwedByManager;
    private CancellationTokenSource? _abandon;
    private StorageOfflineException? _abandonCause;

    // The storage name the coordinator hands over with the engine mode, for the event source's
    // database payload; empty for a standalone lock manager.
    private string _database = string.Empty;

    // How a lock request's wait ended, as the LockWaitStop event names it.
    private const string WaitGranted = "Granted";
    private const string WaitCancelled = "Cancelled";
    private const string WaitEnded = "Ended";
    private const string WaitAbandoned = "Abandoned";

    internal LockManager()
    {
    }

    /// <summary>
    /// Creates a standalone lock manager: a mode-compatibility lock table with FIFO waiter
    /// wake-up and wait-for-graph deadlock detection that aborts the requester whose wait would
    /// close a cycle.
    /// </summary>
    /// <returns>The lock manager.</returns>
    public static LockManager Create() => new();

    /// <summary>
    /// Acquires a lock on the specified resource for the specified transaction,
    /// waiting until the lock is granted, the wait times out, or a deadlock is resolved.
    /// </summary>
    /// <param name="owner">The transaction requesting the lock.</param>
    /// <param name="resource">The resource to lock.</param>
    /// <param name="mode">The requested lock mode.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task that completes when the lock is granted.</returns>
    /// <exception cref="TransactionDeadlockException">Thrown when the request was chosen as a deadlock victim.</exception>
    /// <exception cref="TransactionAbortedException">
    /// Thrown when <see cref="ReleaseAll"/> ended the owner while the request waited; when the
    /// owner's pending requests were abandoned and it does not already hold the resource in a
    /// mode at least as strong as the one requested; or, in the coordinator's engine mode, when
    /// the request had to wait and the database's storage went offline (its inner exception is
    /// the <see cref="StorageOfflineException"/>).
    /// </exception>
    /// <remarks>
    /// In the coordinator's engine mode a request the table grants at once costs no more than a
    /// standalone one. One that has to wait waits on the caller's token and on the storage going
    /// offline, which fails it (<see cref="Abandon"/>).
    /// </remarks>
    public ValueTask AcquireAsync(
        TransactionSequence owner,
        LockResource resource,
        LockMode mode,
        CancellationToken cancellationToken = default)
    {
        if (_abandon is null)
        {
            return AcquireCoreAsync(owner, resource, mode, cancellationToken, cancellationToken);
        }

        return TryAcquire(owner, resource, mode)
            ? ValueTask.CompletedTask
            : WaitAbandonableAsync(owner, resource, mode, cancellationToken);
    }

    /// <summary>
    /// Installs the coordinator's engine mode (see the class remarks): from now on
    /// <see cref="ReleaseAll"/> leaves the release of every owner
    /// <paramref name="releaseOwedByManager"/> reports to the transaction manager, and waits can
    /// be abandoned. Called once, by the coordinator, before the lock manager is shared.
    /// </summary>
    /// <param name="releaseOwedByManager">True for an owner the transaction manager still tracks.</param>
    /// <param name="database">The name of the database whose locks this manager arbitrates, for its diagnostics.</param>
    /// <exception cref="InvalidOperationException">The mode is already installed.</exception>
    internal void EnterEngineMode(Func<ulong, bool> releaseOwedByManager, string database)
    {
        ArgumentNullException.ThrowIfNull(releaseOwedByManager);

        if (_abandon is not null)
        {
            throw new InvalidOperationException("The lock manager is already in engine mode.");
        }

        _releaseOwedByManager = releaseOwedByManager;
        _database = database ?? string.Empty;
        _abandon = new CancellationTokenSource();
    }

    /// <summary>
    /// Ends every wait in progress and fails every later one, because the storage went offline.
    /// Idempotent: the first cause is kept.
    /// </summary>
    /// <param name="cause">The error that took the storage offline.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cause"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The lock manager is not in engine mode.</exception>
    internal void Abandon(StorageOfflineException cause)
    {
        ArgumentNullException.ThrowIfNull(cause);

        var abandon = _abandon ?? throw new InvalidOperationException("Only a lock manager in engine mode abandons its waits.");
        if (Interlocked.CompareExchange(ref _abandonCause, cause, null) is null)
        {
            // Asynchronously: the storage's offline hook may run under its locks.
            _ = abandon.CancelAsync();
            TransactionEventSource.Log.LockWaitsAbandoned(_database, cause);
        }
    }

    private async ValueTask WaitAbandonableAsync(TransactionSequence owner, LockResource resource, LockMode mode, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _abandonCause) is { } offline)
        {
            throw Abandoned(owner, resource, mode, offline);
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abandon!.Token);
        try
        {
            await AcquireCoreAsync(owner, resource, mode, wait.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _abandonCause) is { } cause)
        {
            throw Abandoned(owner, resource, mode, cause);
        }
    }

    private static TransactionAbortedException Abandoned(TransactionSequence owner, LockResource resource, LockMode mode, StorageOfflineException cause)
        => new($"Transaction {owner}'s request for {mode} on {resource} was refused: the database's storage went offline, " +
            "so no lock of it is released until it is reopened.", cause);

    /// <remarks>
    /// An owner whose pending requests were abandoned (<see cref="AbandonPending"/>) is refused
    /// with <see cref="TransactionAbortedException"/> unless it already holds the resource in a
    /// mode at least as strong as the one requested.
    /// </remarks>
    /// <param name="owner">The transaction requesting the lock.</param>
    /// <param name="resource">The resource to lock.</param>
    /// <param name="mode">The requested lock mode.</param>
    /// <param name="cancellationToken">The token the wait observes: the caller's, or in engine mode the caller's linked with the abandonment.</param>
    /// <param name="callerToken">The caller's own token, which tells a canceled wait from an abandoned one in the diagnostics.</param>
    private async ValueTask AcquireCoreAsync(
        TransactionSequence owner,
        LockResource resource,
        LockMode mode,
        CancellationToken cancellationToken,
        CancellationToken callerToken)
    {
        Waiter? waiter = null;

        lock (_sync)
        {
            var entry = GetEntryLocked(resource);

            if (RefuseAbandonedLocked(entry, owner.Value, mode))
            {
                RemoveIfUnusedLocked(resource, entry);
                throw new TransactionAbortedException(
                    $"Transaction {owner} has ended; its request for {mode} on {resource} was refused.");
            }

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
            }
            else
            {
                waiter = new Waiter(owner.Value, mode);
                entry.Waiters.Add(waiter);
            }
        }

        // The victim's refusal is raised after the lock is released, so the event is not written
        // under it; the lock table is already back to its state before the request.
        if (waiter is null)
        {
            TransactionEventSource.Log.DeadlockDetected(_database, owner, resource, mode);
            throw new TransactionDeadlockException(
                $"Transaction {owner} was chosen as the deadlock victim requesting {mode} on {resource}.");
        }

        // A wait already costs a queued waiter and a registration, so its start is timed
        // whether or not anyone listens (plan D5 (b)): a listener that attaches during a long
        // wait still learns how long it lasted. Start and stop are written on this flow.
        long started = Stopwatch.GetTimestamp();
        TransactionEventSource.Log.LockWaitStart(_database, owner, resource, mode);

        try
        {
            // Disposed as soon as the wait ends, before the stop is written, as it was before the
            // wait was traced: a late cancellation cannot reach CancelWaiter during a listener's
            // synchronous dispatch.
            using var registration = cancellationToken.Register(() => CancelWaiter(resource, waiter));
            await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            if (TransactionEventSource.Log.IsEnabled())
            {
                TransactionEventSource.Log.LockWaitStop(_database, owner, resource, mode, WaitOutcome(waiter, callerToken), started);
            }
        }
    }

    /// <summary>
    /// Names how a finished wait ended: granted; canceled by its caller; failed because its owner's
    /// transaction ended (<see cref="FailEnded"/>); or abandoned because the storage went offline,
    /// which <see cref="WaitAbandonableAsync"/> decides the same way.
    /// </summary>
    private string WaitOutcome(Waiter waiter, CancellationToken callerToken) => waiter.Completion.Task.Status switch
    {
        TaskStatus.RanToCompletion => WaitGranted,
        TaskStatus.Canceled when !callerToken.IsCancellationRequested && Volatile.Read(ref _abandonCause) is not null => WaitAbandoned,
        TaskStatus.Canceled => WaitCancelled,
        _ => WaitEnded,
    };

    /// <summary>
    /// Attempts to acquire a lock without waiting.
    /// </summary>
    /// <param name="owner">The transaction requesting the lock.</param>
    /// <param name="resource">The resource to lock.</param>
    /// <param name="mode">The requested lock mode.</param>
    /// <returns>True when the lock was granted immediately; otherwise false.</returns>
    /// <remarks>
    /// Returns false for an owner whose pending requests were abandoned
    /// (<see cref="AbandonPending"/>), unless it already holds the resource in a mode at
    /// least as strong as the one requested.
    /// </remarks>
    public bool TryAcquire(TransactionSequence owner, LockResource resource, LockMode mode)
    {
        lock (_sync)
        {
            var entry = GetEntryLocked(resource);

            if (RefuseAbandonedLocked(entry, owner.Value, mode))
            {
                RemoveIfUnusedLocked(resource, entry);
                return false;
            }

            return TryGrantLocked(entry, owner.Value, mode);
        }
    }

    /// <summary>
    /// Releases every lock held by the specified transaction, and fails its pending
    /// requests with <see cref="TransactionAbortedException"/>: the call ends the
    /// owner's participation in the lock table.
    /// </summary>
    /// <param name="owner">The transaction whose locks are released.</param>
    /// <remarks>
    /// <para>
    /// The owner's own pending requests fail with <see cref="TransactionAbortedException"/>
    /// instead of staying queued: a grant that arrived after the owner ended would hold
    /// the resource for a transaction that can never release it again. Neo4j's lock
    /// client does the same when its transaction is terminated: the wait loop checks the
    /// stopped flag and throws (<c>community/lock/.../forseti/ForsetiClient.java:1081-1085</c>,
    /// checked at <c>:230</c>). A request queued after this call is not seen here, so a
    /// caller that can race an end checks its transaction after the grant and releases it.
    /// </para>
    /// <para>
    /// In the coordinator's engine mode the call releases nothing for a transaction the manager
    /// still tracks. The manager removes a transaction from its active table before it releases
    /// the transaction's locks, so either it still tracks the owner here, and its release comes
    /// later and covers every grant made by now, or it has released already, and this call
    /// releases what was granted since.
    /// </para>
    /// </remarks>
    public void ReleaseAll(TransactionSequence owner)
    {
        if (_releaseOwedByManager is { } owedByManager && owedByManager(owner.Value))
        {
            return;
        }

        ReleaseAllUnfiltered(owner);
    }

    /// <summary>
    /// Releases every lock of the owner and fails its pending requests, whatever the engine
    /// mode: the transaction manager's own release, at the moment the owner leaves its active
    /// table.
    /// </summary>
    /// <param name="owner">The transaction whose locks are released.</param>
    internal void ReleaseAllUnfiltered(TransactionSequence owner)
    {
        List<Waiter> granted = new();
        List<(LockResource Resource, Waiter Waiter)>? ended = null;

        lock (_sync)
        {
            RemoveWaitEdgesLocked(owner.Value);
            _abandoned.Remove(owner.Value);

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
    /// <para>
    /// The owner stays abandoned until <see cref="ReleaseAll"/>: a request it makes in the
    /// meantime (a late operation of the ended transaction) is refused unless the owner
    /// already holds the resource in a mode at least as strong, instead of queuing. A
    /// queued request would join the wait-for graph, where a live transaction could be
    /// chosen as the deadlock victim of a transaction that has already ended, and a new
    /// grant would stay held until the deferred undo completes, because only the manager
    /// releases a tracked transaction's locks.
    /// </para>
    /// </remarks>
    internal void AbandonPending(TransactionSequence owner)
    {
        List<(LockResource Resource, Waiter Waiter)>? ended = null;

        lock (_sync)
        {
            _abandoned.Add(owner.Value);
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

    /// <summary>
    /// Whether a request of an abandoned owner must be refused: everything except a re-grant
    /// of a resource the owner already holds in a mode at least as strong, which changes
    /// nothing for anyone else (the engines' writer-lock helpers re-request the database lock
    /// their transaction holds, then see the end and give the grant back).
    /// </summary>
    private bool RefuseAbandonedLocked(LockEntry entry, ulong owner, LockMode mode)
        => _abandoned.Contains(owner)
            && !(entry.Granted.TryGetValue(owner, out var held) && Strength(held) >= Strength(mode));

    // A refused request may have created the entry it looked up.
    private void RemoveIfUnusedLocked(LockResource resource, LockEntry entry)
    {
        if (entry.Granted.Count == 0 && entry.Waiters.Count == 0)
        {
            _table.Remove(resource);
        }
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
