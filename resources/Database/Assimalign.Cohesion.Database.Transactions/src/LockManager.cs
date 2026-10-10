using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;
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
/// <b>Deadlock detection (#1390).</b> The wait-for graph is read from the table each time a
/// request queues: the requester waits for every owner holding a mode its request conflicts
/// with, and so on through each of those owners' own queued requests. A cycle back to the
/// requester makes it the victim. A grant can add an edge too, from a request queued on the
/// resource to the owner granted it; that closes a cycle only through a request the owner still
/// has queued elsewhere, which then fails as the victim. Every wait-for edge between transactions
/// is a lock of this table: the coordinator's apply gate, the storage's page latches and its page
/// write locks are never held across a wait here (a request that would wait inside a statement
/// apply is refused; see <see cref="AcquireAsync"/>).
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

    // Every resource an owner was granted or queued for, so its release and AbandonPending visit
    // only the owner's entries instead of enumerating the whole table, which cost every transaction
    // end time in proportion to every other transaction's locks and to the table's high-water mark
    // (#1363 review). PostgreSQL's LockReleaseAll walks the backend's own lock table and proc-lock
    // lists the same way, never the shared table (src/backend/storage/lmgr/lock.c:2338, :2377,
    // :2511-2540). A resource stays listed after a canceled wait; the release then finds no grant
    // or request of the owner there.
    private readonly Dictionary<ulong, HashSet<LockResource>> _owned = new();

    // The requests each owner has queued and not yet had granted, failed or canceled. The deadlock
    // detector reads an owner's wait-for edges from these and the grants of their entries as the
    // table stands when it looks, never from a graph recorded when the request queued (#1390): the
    // holders a waiter waits for change while it waits. A release wakes a waiter queued ahead of
    // it and grants it the resource; a compatible request is granted past it; a holder the waiter
    // first waited for releases. Edges recorded at the request named holders that had left and
    // missed every later one, so two transactions could wait for each other with neither edge in
    // the graph, until a cancellation ended one wait. PostgreSQL's detector likewise reads its
    // edges from the lock table each time it runs, under every partition lock (CheckDeadLock,
    // src/backend/storage/lmgr/proc.c:1880-1891): a hard edge to each holder of a conflicting
    // mode, a soft edge to each waiter queued ahead with a conflicting request
    // (FindLockCycleRecurseMember, src/backend/storage/lmgr/deadlock.c:561-583, :624-768).
    private readonly Dictionary<ulong, List<Waiter>> _waiting = new();

    // The cycle search's working sets, reused under _sync so a search allocates nothing.
    private readonly HashSet<ulong> _searchVisited = new();
    private readonly Stack<ulong> _searchFrontier = new();

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

    // True for the flow of a statement apply that holds the coordinator's apply gate, null for a
    // standalone lock manager: a request that would wait there is refused (WaitInsideApply).
    private Func<bool>? _insideApply;

    // How a lock request's wait ended, as the LockWaitStop event names it.
    private const string WaitGranted = "Granted";
    private const string WaitCancelled = "Cancelled";
    private const string WaitEnded = "Ended";
    private const string WaitAbandoned = "Abandoned";
    private const string WaitDeadlock = "Deadlock";

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
    /// <exception cref="InvalidOperationException">
    /// In the coordinator's engine mode, the request had to wait while its caller's flow holds the
    /// coordinator's statement apply gate. Nothing awaited inside the gate may wait: a statement
    /// acquires every lock its apply needs before it takes the gate, so a lock its apply
    /// re-requests is granted at once (see the class remarks).
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
    /// <paramref name="releaseOwedByManager"/> reports to the transaction manager, waits can
    /// be abandoned, and a request that would wait inside a statement apply is refused. Called
    /// once, by the coordinator, before the lock manager is shared.
    /// </summary>
    /// <param name="releaseOwedByManager">True for an owner the transaction manager still tracks.</param>
    /// <param name="insideApply">True while the calling flow holds the coordinator's statement apply gate.</param>
    /// <param name="database">The name of the database whose locks this manager arbitrates, for its diagnostics.</param>
    /// <exception cref="InvalidOperationException">The mode is already installed.</exception>
    internal void EnterEngineMode(Func<ulong, bool> releaseOwedByManager, Func<bool> insideApply, string database)
    {
        ArgumentNullException.ThrowIfNull(releaseOwedByManager);
        ArgumentNullException.ThrowIfNull(insideApply);

        if (_abandon is not null)
        {
            throw new InvalidOperationException("The lock manager is already in engine mode.");
        }

        _releaseOwedByManager = releaseOwedByManager;
        _insideApply = insideApply;
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

        // PostgreSQL never waits for a heavyweight lock while it holds a buffer content lock: an
        // LWLock has no deadlock detection (src/backend/storage/lmgr/README:20-29), so heap_delete
        // unlocks the buffer before it waits for the tuple lock and the deleting transaction
        // (src/backend/access/heap/heapam.c:2920-2923). The apply gate is this kernel's
        // equivalent: every writer, undo and prune of the database waits for it, and it is no
        // lock-table resource, so a request that waited under it would stall the whole database
        // and could close a cycle the detector never sees. The executors acquire every lock their
        // apply needs before they take the gate; a request that must wait inside it breaks that
        // order and is refused at once.
        if (_insideApply!())
        {
            throw WaitInsideApply(owner, resource, mode);
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

    private static InvalidOperationException WaitInsideApply(TransactionSequence owner, LockResource resource, LockMode mode)
        => new($"Transaction {owner}'s request for {mode} on {resource} would wait inside a statement apply, which holds the " +
            "database's apply gate; the request was refused. A statement acquires every lock its apply needs before the apply begins.");

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
        Waiter? waiter;
        List<Waiter>? victims = null;
        bool deadlocked = false;

        lock (_sync)
        {
            var entry = GetEntryLocked(resource);

            if (RefuseAbandonedLocked(entry, owner.Value, mode))
            {
                RemoveIfUnusedLocked(resource, entry);
                throw new TransactionAbortedException(
                    $"Transaction {owner} has ended; its request for {mode} on {resource} was refused.");
            }

            if (TryGrantLocked(resource, entry, owner.Value, mode, ref victims))
            {
                waiter = null;
            }
            else
            {
                // Queue the request, then look for a cycle through it in the table as it stands:
                // one means granting can never happen without an abort, and the requester is the
                // victim.
                waiter = new Waiter(this, owner.Value, mode, resource, entry);
                EnqueueLocked(waiter);

                if (ClosesCycleLocked(owner.Value))
                {
                    DequeueLocked(waiter);
                    RemoveIfUnusedLocked(resource, entry);
                    deadlocked = true;
                }
            }
        }

        if (waiter is null)
        {
            FailDeadlocked(victims);
            return;
        }

        // The victim's refusal is raised after the lock is released, so the event is not written
        // under it; the lock table is already back to its state before the request.
        if (deadlocked)
        {
            TransactionEventSource.Log.DeadlockDetected(_database, owner, resource, mode);
            throw Deadlock(owner, resource, mode);
        }

        // A wait already costs a queued waiter and a registration, so its start is timed
        // whether or not anyone listens (plan D5 (b)): a listener that attaches during a long
        // wait still learns how long it lasted. Start and stop are written on this flow.
        long started = Stopwatch.GetTimestamp();
        bool startWritten = TransactionEventSource.Log.LockWaitStart(_database, owner, resource, mode);

        // Armed only while a listener takes warnings, and disposed when the wait ends.
        using var blockedReport = BlockedWaitReport.Arm(this, waiter, started);

        try
        {
            // Disposed as soon as the wait ends, before the stop is written, as it was before the
            // wait was traced: a late cancellation cannot reach CancelWaiter during a listener's
            // synchronous dispatch. The callback is static and the waiter its state, so a request
            // granted at once allocates no closure for a registration it never makes.
            using var registration = cancellationToken.Register(static state => ((Waiter)state!).Cancel(), waiter);
            await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            if (TransactionEventSource.Log.IsEnabled())
            {
                TransactionEventSource.Log.LockWaitStop(_database, owner, resource, mode, WaitOutcome(waiter, callerToken), startWritten, started);
            }
        }
    }

    /// <summary>
    /// Names how a finished wait ended: granted; canceled by its caller; failed because its owner's
    /// transaction ended (<see cref="FailEnded"/>); chosen as a deadlock victim after a grant
    /// closed a cycle through it (<see cref="FailDeadlocked"/>); or abandoned because the storage
    /// went offline, which <see cref="WaitAbandonableAsync"/> decides the same way.
    /// </summary>
    private string WaitOutcome(Waiter waiter, CancellationToken callerToken) => waiter.Completion.Task.Status switch
    {
        TaskStatus.RanToCompletion => WaitGranted,
        TaskStatus.Canceled when !callerToken.IsCancellationRequested && Volatile.Read(ref _abandonCause) is not null => WaitAbandoned,
        TaskStatus.Canceled => WaitCancelled,
        _ when waiter.Completion.Task.Exception?.InnerException is TransactionDeadlockException => WaitDeadlock,
        _ => WaitEnded,
    };

    private static TransactionDeadlockException Deadlock(TransactionSequence owner, LockResource resource, LockMode mode)
        => new($"Transaction {owner} was chosen as the deadlock victim requesting {mode} on {resource}.");

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
        List<Waiter>? victims = null;
        bool granted;

        lock (_sync)
        {
            var entry = GetEntryLocked(resource);

            if (RefuseAbandonedLocked(entry, owner.Value, mode))
            {
                RemoveIfUnusedLocked(resource, entry);
                return false;
            }

            granted = TryGrantLocked(resource, entry, owner.Value, mode, ref victims);
        }

        FailDeadlocked(victims);
        return granted;
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
        List<Waiter>? granted = null;
        List<Waiter>? ended = null;
        List<Waiter>? victims = null;

        lock (_sync)
        {
            _abandoned.Remove(owner.Value);
            DequeueOwnerLocked(owner.Value, ref ended);

            // Only the owner's entries can change: a grant depends on the modes held, never on the
            // queue, so no waiter elsewhere becomes grantable when this owner leaves.
            if (_owned.Remove(owner.Value, out var resources))
            {
                foreach (var resource in resources)
                {
                    if (!_table.TryGetValue(resource, out var entry))
                    {
                        continue;
                    }

                    entry.Granted.Remove(owner.Value);

                    // Wake compatible waiters in FIFO order. A grant takes its request off the
                    // queue; one that fails a deadlock victim queued here starts the walk over.
                    for (int i = 0; i < entry.Waiters.Count;)
                    {
                        var waiter = entry.Waiters[i];
                        int failed = victims?.Count ?? 0;

                        if (TryGrantLocked(resource, entry, waiter.Owner, waiter.Mode, ref victims, waiter))
                        {
                            (granted ??= new()).Add(waiter);

                            if ((victims?.Count ?? 0) != failed)
                            {
                                i = 0;
                            }
                        }
                        else
                        {
                            i++;
                        }
                    }

                    RemoveIfUnusedLocked(resource, entry);
                }
            }
        }

        if (granted is not null)
        {
            foreach (var waiter in granted)
            {
                waiter.Completion.TrySetResult();
            }
        }

        FailEnded(owner, ended);
        FailDeadlocked(victims);
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
        List<Waiter>? ended = null;

        lock (_sync)
        {
            _abandoned.Add(owner.Value);
            DequeueOwnerLocked(owner.Value, ref ended);
        }

        FailEnded(owner, ended);
    }

    private static void FailEnded(TransactionSequence owner, List<Waiter>? ended)
    {
        if (ended is null)
        {
            return;
        }

        foreach (var waiter in ended)
        {
            waiter.Completion.TrySetException(new TransactionAbortedException(
                $"Transaction {owner} ended while it waited for {waiter.Mode} on {waiter.Resource}; the request was not granted."));
        }
    }

    /// <summary>
    /// Fails the queued requests a grant left on a wait-for cycle (<see cref="FailCyclesThroughLocked"/>)
    /// with <see cref="TransactionDeadlockException"/>, outside the lock.
    /// </summary>
    private void FailDeadlocked(List<Waiter>? victims)
    {
        if (victims is null)
        {
            return;
        }

        foreach (var waiter in victims)
        {
            var owner = new TransactionSequence(waiter.Owner);
            TransactionEventSource.Log.DeadlockDetected(_database, owner, waiter.Resource, waiter.Mode);
            waiter.Completion.TrySetException(Deadlock(owner, waiter.Resource, waiter.Mode));
        }
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_sync)
        {
            // A request already granted, failed or canceled has left its queue.
            if (waiter.Entry.Waiters.Remove(waiter))
            {
                UntrackWaitLocked(waiter);
                RemoveIfUnusedLocked(waiter.Resource, waiter.Entry);
            }
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

    // A refused request, a dequeued waiter or a release may have left the entry with nothing in it.
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
    /// owners (an owner's own grants never block it — upgrades are supported). A first grant of
    /// the resource to the owner lists it among the owner's resources.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="entry">The resource's entry.</param>
    /// <param name="owner">The owner requesting the grant.</param>
    /// <param name="mode">The requested mode.</param>
    /// <param name="victims">Collects the queued requests the grant leaves on a wait-for cycle, failed by the caller outside the lock.</param>
    /// <param name="queued">The owner's queued request this grant answers, which leaves its queue; null for a new request.</param>
    private bool TryGrantLocked(LockResource resource, LockEntry entry, ulong owner, LockMode mode, ref List<Waiter>? victims, Waiter? queued = null)
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

        bool stronger;
        if (entry.Granted.TryGetValue(owner, out var existing))
        {
            // Keep the strongest mode held.
            stronger = Strength(mode) > Strength(existing);
            if (stronger)
            {
                entry.Granted[owner] = mode;
            }
        }
        else
        {
            stronger = true;
            entry.Granted[owner] = mode;
            TrackLocked(owner, resource);
        }

        if (queued is not null)
        {
            entry.Waiters.Remove(queued);
            UntrackWaitLocked(queued);
        }

        // A grant adds wait-for edges when it gives the owner a mode that another owner's request
        // queued here conflicts with. The search a request runs when it queues cannot see them,
        // because they did not exist then. They can close a cycle only through a request the owner
        // itself still has queued elsewhere (an owner with no queued request is not waiting), so
        // only then does the grant search, from each of those requests.
        if (stronger && _waiting.Count != 0 && _waiting.ContainsKey(owner) && HasConflictingWaiterLocked(entry, owner, entry.Granted[owner]))
        {
            FailCyclesThroughLocked(owner, ref victims);
        }

        return true;
    }

    /// <summary>
    /// Lists a resource the owner was granted or queued for, for its release.
    /// </summary>
    private void TrackLocked(ulong owner, LockResource resource)
    {
        if (!_owned.TryGetValue(owner, out var resources))
        {
            resources = new HashSet<LockResource>();
            _owned[owner] = resources;
        }

        resources.Add(resource);
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

    /// <summary>
    /// Queues a request: on its entry, among its owner's queued requests (the detector's edges),
    /// and among its owner's resources (the release's walk).
    /// </summary>
    private void EnqueueLocked(Waiter waiter)
    {
        waiter.Entry.Waiters.Add(waiter);

        if (!_waiting.TryGetValue(waiter.Owner, out var queued))
        {
            queued = new List<Waiter>(1);
            _waiting[waiter.Owner] = queued;
        }

        queued.Add(waiter);
        TrackLocked(waiter.Owner, waiter.Resource);
    }

    /// <summary>Takes a queued request off its entry and its owner's queued requests.</summary>
    private void DequeueLocked(Waiter waiter)
    {
        waiter.Entry.Waiters.Remove(waiter);
        UntrackWaitLocked(waiter);
    }

    private void UntrackWaitLocked(Waiter waiter)
    {
        if (_waiting.TryGetValue(waiter.Owner, out var queued) && queued.Remove(waiter) && queued.Count == 0)
        {
            _waiting.Remove(waiter.Owner);
        }
    }

    /// <summary>
    /// Takes every queued request of the owner off its entry, for the caller to fail outside the
    /// lock. An entry left empty is removed.
    /// </summary>
    private void DequeueOwnerLocked(ulong owner, ref List<Waiter>? dequeued)
    {
        if (!_waiting.Remove(owner, out var queued))
        {
            return;
        }

        foreach (var waiter in queued)
        {
            waiter.Entry.Waiters.Remove(waiter);
            RemoveIfUnusedLocked(waiter.Resource, waiter.Entry);
            (dequeued ??= new()).Add(waiter);
        }
    }

    // Whether another owner's queued request on the entry conflicts with the mode the owner holds:
    // the edge a grant adds, from that request to the owner.
    private static bool HasConflictingWaiterLocked(LockEntry entry, ulong owner, LockMode held)
    {
        foreach (var waiter in entry.Waiters)
        {
            if (waiter.Owner != owner && !_compatible[(int)held, (int)waiter.Mode])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// After a grant to an owner that still has requests queued elsewhere: fails each of those
    /// requests from which the owner can reach itself in the wait-for graph. Failing them breaks
    /// every cycle the grant closed, because each such cycle leaves the owner through one of them.
    /// </summary>
    private void FailCyclesThroughLocked(ulong owner, ref List<Waiter>? victims)
    {
        var queued = _waiting[owner];

        for (int i = queued.Count - 1; i >= 0; i--)
        {
            var waiter = queued[i];
            var frontier = _searchFrontier;
            frontier.Clear();
            PushBlockersLocked(waiter, frontier);

            if (ReachesLocked(owner))
            {
                DequeueLocked(waiter);
                RemoveIfUnusedLocked(waiter.Resource, waiter.Entry);
                (victims ??= new()).Add(waiter);
            }
        }
    }

    /// <summary>
    /// Whether the owner's queued requests close a cycle: whether the owner is reachable in the
    /// wait-for graph from the owners it waits for.
    /// </summary>
    /// <remarks>
    /// The graph is read from the table as it stands: an owner waits for every other owner that
    /// holds, in a conflicting mode, a resource one of its queued requests is for. Those are the
    /// only edges there are, because a grant depends on the modes held and never on the queue, so
    /// no queued request waits for one queued ahead of it. PostgreSQL's soft edges
    /// (<c>src/backend/storage/lmgr/deadlock.c:624-768</c>) exist because its grants respect the
    /// queue: <c>LockAcquireExtended</c> joins the queue behind a conflicting queued request
    /// (<c>src/backend/storage/lmgr/lock.c:1135-1143</c>). Here they would be edges no grant
    /// observes, and a cycle through one would abort a transaction that was never stuck.
    /// </remarks>
    private bool ClosesCycleLocked(ulong owner)
    {
        var frontier = _searchFrontier;
        frontier.Clear();

        if (_waiting.TryGetValue(owner, out var queued))
        {
            foreach (var waiter in queued)
            {
                PushBlockersLocked(waiter, frontier);
            }
        }

        return ReachesLocked(owner);
    }

    // Depth-first search from the frontier, following each owner's current edges.
    private bool ReachesLocked(ulong target)
    {
        var frontier = _searchFrontier;
        var visited = _searchVisited;
        visited.Clear();

        try
        {
            while (frontier.Count > 0)
            {
                ulong current = frontier.Pop();

                if (current == target)
                {
                    return true;
                }

                if (!visited.Add(current) || !_waiting.TryGetValue(current, out var queued))
                {
                    continue;
                }

                foreach (var waiter in queued)
                {
                    PushBlockersLocked(waiter, frontier);
                }
            }

            return false;
        }
        finally
        {
            frontier.Clear();
            visited.Clear();
        }
    }

    // Pushes the owners whose grants conflict with a queued request: the request's edges.
    private static void PushBlockersLocked(Waiter waiter, Stack<ulong> frontier)
    {
        foreach (var (holder, heldMode) in waiter.Entry.Granted)
        {
            if (holder != waiter.Owner && !_compatible[(int)heldMode, (int)waiter.Mode])
            {
                frontier.Push(holder);
            }
        }
    }

    /// <summary>
    /// Writes <c>LockWaitBlocked</c> for a request that is still queued: the transactions it waits
    /// for, with the mode each holds, and the requests each of those has queued in turn, with
    /// their own blockers. Read under the lock, written after it.
    /// </summary>
    private void ReportBlocked(Waiter waiter, long started)
    {
        string blockers;
        lock (_sync)
        {
            if (!waiter.Entry.Waiters.Contains(waiter))
            {
                return;
            }

            var description = new StringBuilder();
            DescribeBlockersLocked(waiter, description, depth: 0);
            blockers = description.Length == 0 ? "none" : description.ToString();
        }

        TransactionEventSource.Log.LockWaitBlocked(_database, new TransactionSequence(waiter.Owner), waiter.Resource, waiter.Mode, started, blockers);
    }

    // The holders a request waits for, as "<sequence>:<mode held>", each followed by what it waits
    // for itself, one level down: "1:Exclusive waiting for Shared on Entry:2 (2:Exclusive)". A
    // cycle of two shows in full; each waiter of a longer chain reports its own step.
    private void DescribeBlockersLocked(Waiter waiter, StringBuilder description, int depth)
    {
        foreach (var (holder, held) in waiter.Entry.Granted)
        {
            if (holder == waiter.Owner || _compatible[(int)held, (int)waiter.Mode])
            {
                continue;
            }

            if (description.Length != 0 && description[^1] != '(')
            {
                description.Append(", ");
            }

            description.Append(holder.ToString(CultureInfo.InvariantCulture)).Append(':').Append(held.ToString());

            if (depth == 0 && _waiting.TryGetValue(holder, out var queued))
            {
                foreach (var request in queued)
                {
                    description.Append(" waiting for ").Append(request.Mode.ToString()).Append(" on ")
                        .Append(TransactionEventSource.DescribeResource(request.Resource)).Append(" (");
                    DescribeBlockersLocked(request, description, depth + 1);
                    description.Append(')');
                }
            }
        }
    }

    /// <summary>
    /// The one-shot timer that reports a wait still queued at the slow-lock-wait threshold
    /// (<see cref="ReportBlocked"/>). Created only while a listener takes the source's warnings, so
    /// a wait costs nothing more when nobody listens.
    /// </summary>
    private sealed class BlockedWaitReport : IDisposable
    {
        private readonly LockManager _manager;
        private readonly Waiter _waiter;
        private readonly long _started;
        private readonly Timer _timer;

        private BlockedWaitReport(LockManager manager, Waiter waiter, long started, TimeSpan threshold)
        {
            _manager = manager;
            _waiter = waiter;
            _started = started;
            _timer = new Timer(static state => ((BlockedWaitReport)state!).Report(), this, threshold, Timeout.InfiniteTimeSpan);
        }

        // A timer's longest due time; a threshold past it (about 49 days) reports nothing.
        private const double LongestDueMilliseconds = uint.MaxValue - 1;

        public static BlockedWaitReport? Arm(LockManager manager, Waiter waiter, long started)
        {
            if (!TransactionEventSource.Log.IsEnabled(EventLevel.Warning, EventKeywords.None))
            {
                return null;
            }

            double threshold = TransactionEventSource.Log.SlowLockWaitThresholdMilliseconds;
            return threshold <= LongestDueMilliseconds
                ? new BlockedWaitReport(manager, waiter, started, TimeSpan.FromMilliseconds(threshold))
                : null;
        }

        public void Dispose() => _timer.Dispose();

        private void Report() => _manager.ReportBlocked(_waiter, _started);
    }

    private sealed class LockEntry
    {
        public Dictionary<ulong, LockMode> Granted { get; } = new();

        public List<Waiter> Waiters { get; } = new();
    }

    private sealed class Waiter
    {
        private readonly LockManager _manager;

        public Waiter(LockManager manager, ulong owner, LockMode mode, LockResource resource, LockEntry entry)
        {
            _manager = manager;
            Owner = owner;
            Mode = mode;
            Resource = resource;
            Entry = entry;
        }

        public ulong Owner { get; }

        public LockMode Mode { get; }

        public LockResource Resource { get; }

        // The entry the request is queued on. An entry with a queued request stays in the table,
        // so this is the table's entry for the resource for as long as the request is queued.
        public LockEntry Entry { get; }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The wait's cancellation callback.
        public void Cancel() => _manager.CancelWaiter(this);
    }
}
