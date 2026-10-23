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
/// by <see cref="LockResource"/> with a mode-compatibility matrix, a FIFO queue per resource,
/// and wait-for-graph detection that repairs soft queue cycles before choosing a hard-cycle victim.
/// </summary>
/// <remarks>
/// <para>
/// MVCC keeps readers lock-free; the lock manager arbitrates writers. Locks are owned by
/// transaction sequences and are released as a set at commit or rollback. Before applying any
/// writes, a statement may use <see cref="MarkStatement"/> and <see cref="ReleaseSince"/> to
/// release only its new resources and retry arbitration. A request whose wait
/// would close an unavoidable held-lock cycle is chosen as the victim and fails with
/// <see cref="TransactionDeadlockException"/>.
/// </para>
/// <para>
/// <b>Grants (#1390).</b> An owner keeps every mode it was granted on a resource, and a request
/// is granted when no other owner holds a mode it conflicts with and no request of another owner
/// queued ahead of it conflicts with it, so a stream of compatible grants cannot pass a waiting
/// request for as long as the stream lasts. A request that waits joins the end of the queue,
/// except that one of an owner already holding the resource goes ahead of the first queued
/// request that waits for that owner, which is what grants a re-grant at once. A release, and a
/// request that leaves the queue without a grant, wake the requests queued on the resource in
/// FIFO order. This is PostgreSQL's lock manager.
/// </para>
/// <para>
/// <b>Deadlock detection (#1390).</b> The wait-for graph is read from the table each time a
/// request queues: the requester waits for every owner holding a mode its request conflicts
/// with, and for the owner of every conflicting request queued ahead of it; the search follows
/// each of those owners' own queued requests the same way. A cycle first tries queue
/// reordering; a hard held-lock cycle makes the requester the victim. A grant can add an edge too, from a request queued on the resource to the
/// owner granted it, but with the queue's places that request already waits for the owner, so
/// no grant closes a cycle; a grant to an owner with a request queued still searches from it,
/// and a request found on a cycle would fail as the victim. Every wait-for edge between
/// transactions is a lock of this table: the coordinator's apply gate, the storage's page
/// latches and its page write locks are never held across a wait here (a request that would
/// wait inside a statement apply is refused; see <see cref="AcquireAsync"/>).
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
    private static readonly string _errorLockManagerFailEndedFailure = ErrorMessages.LockManagerFailEndedFailure;

    private static readonly string _errorLockManagerAcquireCoreAsyncFailure = ErrorMessages.LockManagerAcquireCoreAsyncFailure;

    private static readonly string _errorLockManagerAbandonedFailure = ErrorMessages.LockManagerAbandonedFailure;

    // Compatibility matrix indexed [held, requested]:
    // Shared, Update, Exclusive, IntentShared, IntentExclusive. It is symmetric.
    private static readonly bool[,] _compatible =
    {
        //               S      U      X      IS     IX
        /* S  */ { true,  true,  false, true,  false },
        /* U  */ { true,  false, false, true,  false },
        /* X  */ { false, false, false, false, false },
        /* IS */ { true,  true,  false, true,  true  },
        /* IX */ { false, false, false, true,  true  },
    };

    private const int ModeCount = 5;
    private const byte AllModes = (1 << ModeCount) - 1;

    // The modes each mode conflicts with, as a mask with bit (int)mode set for each: a row of the
    // matrix. The matrix is symmetric, so a row is also the held modes a request for the mode
    // conflicts with.
    private static readonly byte[] _conflicts = BuildConflicts();

    // For each mask of modes an owner holds, the requested modes it conflicts with: the union of
    // its modes' rows.
    private static readonly byte[] _heldConflicts = BuildHeldConflicts();

    private readonly Dictionary<LockResource, LockEntry> _table = new();

    // First grants have a monotonic stamp. Re-grants and upgrades keep it, so a statement
    // mark records the existing holdings without copying them or allocating on the fast path.
    private ulong _grantSequence;

    // Every resource an owner was granted or queued for, so its release and AbandonPending visit
    // only the owner's entries instead of enumerating the whole table, which cost every transaction
    // end time in proportion to every other transaction's locks and to the table's high-water mark
    // (#1363 review). PostgreSQL's LockReleaseAll walks the backend's own lock table and proc-lock
    // lists the same way, never the shared table (src/backend/storage/lmgr/lock.c:2338, :2377,
    // :2511-2540). A resource stays listed after a canceled wait; the release then finds no grant
    // or request of the owner there.
    private readonly Dictionary<ulong, HashSet<LockResource>> _owned = new();

    // The requests each owner has queued and not yet had granted, failed or canceled. The deadlock
    // detector reads an owner's wait-for edges from these, the grants of their entries and the
    // requests queued ahead of them, as the table stands when it looks, never from a graph recorded
    // when the request queued (#1390): the owners a waiter waits for change while it waits. A
    // release grants the resource to a request queued ahead of it; a holder is granted a stronger
    // mode past it; a holder it first waited for releases. Edges recorded at the request named
    // holders that had left and missed every later one, so two transactions could wait for each
    // other with neither edge in the graph, until a cancellation ended one wait. PostgreSQL's
    // detector likewise reads its edges from the lock table each time it runs, under every
    // partition lock (CheckDeadLock, src/backend/storage/lmgr/proc.c:1880-1891): a hard edge to
    // each holder of a conflicting mode, a soft edge to each waiter queued ahead with a conflicting
    // request (FindLockCycleRecurseMember, src/backend/storage/lmgr/deadlock.c:561-583, :624-768).
    private readonly Dictionary<ulong, List<Waiter>> _waiting = new();

    // The cycle search's working sets, reused under _sync so a search allocates nothing.
    private readonly HashSet<ulong> _searchVisited = new();
    private readonly Stack<ulong> _searchFrontier = new();

    // The entries whose queues must be walked again before _sync is released, because a request
    // queued there may have become grantable: a holder left, a request left the queue without a
    // grant, or an owner with a request queued there became a holder (WakeScheduledLocked). Reused
    // under _sync, and empty whenever _sync is free.
    private readonly List<LockEntry> _wake = new();

    // Owners whose transaction ended while their grants stay held (AbandonPending), until
    // ReleaseAll releases them. Such an owner gets nothing new: see RefuseAbandonedLocked.
    private readonly HashSet<ulong> _abandoned = new();
    private readonly object _sync = new();

    // The coordinator's engine mode, null for a standalone lock manager: whether the transaction
    // manager still owes an owner's release (ReleaseAll then leaves it to the manager), and the
    // source canceled, with its cause kept beside it, once the storage went offline (Abandon).
    private Func<ulong, bool>? _releaseOwedByManager;
    private CancellationTokenSource? _abandon;
    private readonly AsyncLocal<TimeSpan> _lockTimeout = new();
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
    private const string WaitTimedOut = "Timeout";
    private const string WaitEnded = "Ended";
    private const string WaitAbandoned = "Abandoned";
    private const string WaitDeadlock = "Deadlock";

    // The most blockers a LockWaitBlocked report names for the waiting request, for each blocker
    // it describes in turn, and the most queued requests it lists for a blocker. The report is
    // built under _sync, and ETW drops an event larger than 64 KB, so it is bounded; the rest is
    // counted ("... (+N more)").
    private const int MaxBlockersDescribed = 16;
    private const int MaxNestedBlockersDescribed = 4;
    private const int MaxQueuedDescribed = 4;

    /// <summary>Initializes a standalone FIFO lock table with deadlock detection.</summary>
    public LockManager()
    {
    }

    // Per-manager test seams; all run outside _sync.
    internal Action<CancellationTokenSource>? WaitTimerArmed { get; set; }
    internal Action? BeforeWaitOutcome { get; set; }
    internal Action? BeforeGrantNotification { get; set; }

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
    /// <exception cref="TransactionLockTimeoutException">The timeout established by <see cref="EnterLockTimeout"/> expired for this wait.</exception>
    /// <exception cref="TransactionAbortedException">
    /// Thrown when <see cref="ReleaseAll"/> ended the owner while the request waited; when the
    /// owner's pending requests were abandoned and the modes it already holds on the resource do
    /// not cover the one requested; or, in the coordinator's engine mode, when the request had to
    /// wait and the database's storage went offline (its inner exception is the
    /// <see cref="StorageOfflineException"/>).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// In the coordinator's engine mode, the request had to wait while its caller's flow holds the
    /// coordinator's statement apply gate. Nothing awaited inside the gate may wait: a statement
    /// acquires every lock its apply needs before it takes the gate, so a lock its apply
    /// re-requests is granted at once (see the class remarks).
    /// </exception>
    /// <remarks>
    /// <para>
    /// The request is granted when no other owner holds a mode it conflicts with and no other
    /// owner's request with a conflicting mode is queued ahead of its place: the end of the
    /// resource's queue or, for an owner that already holds the resource, just ahead of the first
    /// queued request that waits for it. Otherwise it waits there.
    /// </para>
    /// <para>
    /// In the coordinator's engine mode a request the table grants at once costs no more than a
    /// standalone one. One that has to wait observes the caller's token, any scoped lock timeout,
    /// and the storage going offline, which fails it (<see cref="Abandon"/>).
    /// </para>
    /// </remarks>
    public ValueTask AcquireAsync(
        TransactionSequence owner,
        LockResource resource,
        LockMode mode,
        CancellationToken cancellationToken = default)
    {
        if (_abandon is null && _lockTimeout.Value == TimeSpan.Zero)
        {
            return AcquireCoreAsync(owner, resource, mode, cancellationToken);
        }

        return TryAcquire(owner, resource, mode)
            ? ValueTask.CompletedTask
            : WaitAbandonableAsync(owner, resource, mode, cancellationToken);
    }

    /// <summary>
    /// Sets the timeout for each lock wait in the current execution flow until the returned scope
    /// is disposed. Nested scopes restore the previous value; concurrent flows remain independent.
    /// Zero disables the timeout. Uncontended grants do not create a timer.
    /// </summary>
    /// <param name="timeout">A non-negative duration of at most int.MaxValue milliseconds.</param>
    /// <returns>A scope disposed on the entering flow, in reverse nesting order.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is outside the supported range.</exception>
    /// <remarks>
    /// The scope covers nested index acquisitions as well as direct requests, in standalone and
    /// engine modes. A timeout removes the pending request and throws
    /// <see cref="TransactionLockTimeoutException"/>; grants the owner already holds stay held.
    /// </remarks>
    public IDisposable EnterLockTimeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeout, TimeSpan.FromMilliseconds(int.MaxValue));
        var scope = new LockTimeoutScope(this, _lockTimeout.Value);
        _lockTimeout.Value = timeout;
        return scope;
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
            throw new InvalidOperationException(ErrorMessages.LockManagerEnterEngineModeFailure);
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

        var abandon = _abandon ?? throw new InvalidOperationException(ErrorMessages.LockManagerAbandonFailure);
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
        if (_insideApply?.Invoke() == true)
        {
            throw WaitInsideApply(owner, resource, mode);
        }

        // Lock/latch order: wait before the coordinator's apply gate and before storage/index
        // latches. Timer cancellation only takes _sync; dispose registrations/timers after leaving
        // _sync, so a callback draining there cannot deadlock with a grant or release.
        var timeout = _lockTimeout.Value;
        using var timer = timeout > TimeSpan.Zero ? new CancellationTokenSource(timeout) : null;
        if (timer is not null) { WaitTimerArmed?.Invoke(timer); }
        // Separate callbacks carry their cause into the queue arbitration. A later deadline
        // cannot relabel a completed wait while its continuation is delayed (PostgreSQL
        // src/backend/tcop/postgres.c:3590-3600 likewise reports the earlier timeout).
        await AcquireCoreAsync(owner, resource, mode, cancellationToken, timer?.Token ?? default,
            _abandon?.Token ?? default).ConfigureAwait(false);
    }

    private static TransactionAbortedException Abandoned(TransactionSequence owner, LockResource resource, LockMode mode, StorageOfflineException cause)
        => new(string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorLockManagerAbandonedFailure, owner, mode, resource), cause);

    private static InvalidOperationException WaitInsideApply(TransactionSequence owner, LockResource resource, LockMode mode)
        => new(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.LockManagerWaitInsideApplyFailure, owner, mode, resource));

    /// <remarks>
    /// An owner whose pending requests were abandoned (<see cref="AbandonPending"/>) is refused
    /// with <see cref="TransactionAbortedException"/> unless the modes it already holds on the
    /// resource cover the one requested.
    /// </remarks>
    /// <param name="owner">The transaction requesting the lock.</param>
    /// <param name="resource">The resource to lock.</param>
    /// <param name="mode">The requested lock mode.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <param name="timeoutToken">The individual lock timer's token.</param>
    /// <param name="abandonToken">The storage abandonment token.</param>
    private async ValueTask AcquireCoreAsync(
        TransactionSequence owner,
        LockResource resource,
        LockMode mode,
        CancellationToken cancellationToken,
        CancellationToken timeoutToken = default,
        CancellationToken abandonToken = default)
    {
        Waiter? waiter;
        List<Waiter>? granted = null;
        List<Waiter>? victims = null;
        bool deadlocked = false;

        lock (_sync)
        {
            var entry = GetEntryLocked(resource);

            if (RefuseAbandonedLocked(entry, owner.Value, mode))
            {
                RemoveIfUnusedLocked(entry);
                throw new TransactionAbortedException(
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorLockManagerAcquireCoreAsyncFailure, owner, mode, resource));
            }

            if (CanGrantNewLocked(entry, owner.Value, mode, out int place))
            {
                GrantLocked(entry, owner.Value, mode, queued: null, ref victims);
                WakeScheduledLocked(ref granted, ref victims);
                waiter = null;
            }
            else
            {
                // Queue the request at its place, then look for a cycle through it in the table as
                // it stands. First repair reversible queue edges; only an unavoidable
                // held-lock cycle needs a victim. Every new edge starts or ends at its
                // owner, so the search from the owner sees every cycle it closed. Taking it off
                // again restores the queue as it was, so it unblocks nobody.
                waiter = new Waiter(this, owner.Value, mode, entry, cancellationToken);
                EnqueueLocked(waiter, place);

                if (ClosesCycleLocked(owner.Value))
                {
                    if (TryReorderQueuesLocked())
                    {
                        WakeScheduledLocked(ref granted, ref victims);
                    }
                    else
                    {
                        DequeueLocked(waiter);
                        RemoveIfUnusedLocked(entry);
                        deadlocked = true;
                    }
                }
            }
        }

        if (waiter is null)
        {
            CompleteGranted(granted);
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

        // A soft-cycle repair may have granted existing waiters, including this one.
        // Notify outside _sync, as in every other grant path.
        CompleteGranted(granted);
        FailDeadlocked(victims);

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
            using var registration = cancellationToken.Register(static state => ((Waiter)state!).Cancel(WaitCancelled), waiter);
            using var timeoutRegistration = timeoutToken.Register(static state => ((Waiter)state!).Cancel(WaitTimedOut), waiter);
            using var abandonRegistration = abandonToken.Register(static state => ((Waiter)state!).Cancel(WaitAbandoned), waiter);
            await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            BeforeWaitOutcome?.Invoke();
            if (TransactionEventSource.Log.IsEnabled())
            {
                TransactionEventSource.Log.LockWaitStop(_database, owner, resource, mode, WaitOutcome(waiter), startWritten, started);
            }
        }
    }

    /// <summary>
    /// Names how a finished wait ended: granted; canceled by its caller; timed out; failed because its owner's
    /// transaction ended (<see cref="FailEnded"/>); chosen as a deadlock victim after a grant
    /// closed a cycle through it (<see cref="FailDeadlocked"/>); or abandoned because the storage
    /// went offline. Cancellation callbacks preserve the cause that removed the request.
    /// </summary>
    private static string WaitOutcome(Waiter waiter) => waiter.CancellationOutcome ?? (waiter.Completion.Task.Status switch
    {
        TaskStatus.RanToCompletion => WaitGranted,
        _ when waiter.Completion.Task.Exception?.InnerException is TransactionDeadlockException => WaitDeadlock,
        _ => WaitEnded,
    });

    private static TransactionDeadlockException Deadlock(TransactionSequence owner, LockResource resource, LockMode mode)
        => new(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.LockManagerDeadlockFailure, owner, mode, resource));

    /// <summary>
    /// Attempts to acquire a lock without waiting.
    /// </summary>
    /// <param name="owner">The transaction requesting the lock.</param>
    /// <param name="resource">The resource to lock.</param>
    /// <param name="mode">The requested lock mode.</param>
    /// <returns>True when the lock was granted immediately; otherwise false.</returns>
    /// <remarks>
    /// Grants on the terms <see cref="AcquireAsync"/> does: a request that would queue behind
    /// another owner's queued request returns false. Returns false for an owner whose pending
    /// requests were abandoned (<see cref="AbandonPending"/>), unless the modes it already holds
    /// on the resource cover the one requested.
    /// </remarks>
    public bool TryAcquire(TransactionSequence owner, LockResource resource, LockMode mode)
    {
        List<Waiter>? granted = null;
        List<Waiter>? victims = null;

        lock (_sync)
        {
            var entry = GetEntryLocked(resource);

            if (RefuseAbandonedLocked(entry, owner.Value, mode) || !CanGrantNewLocked(entry, owner.Value, mode, out _))
            {
                RemoveIfUnusedLocked(entry);
                return false;
            }

            GrantLocked(entry, owner.Value, mode, queued: null, ref victims);
            WakeScheduledLocked(ref granted, ref victims);
        }

        CompleteGranted(granted);
        FailDeadlocked(victims);
        return true;
    }

    /// <summary>
    /// Marks the grants already held by an owner before an unwritten statement takes locks.
    /// </summary>
    /// <param name="owner">The transaction whose existing grants must be preserved.</param>
    /// <returns>An allocation-free mark for this manager and owner.</returns>
    /// <remarks>The caller serializes statements and lock requests within the owner.</remarks>
    public LockStatementMark MarkStatement(TransactionSequence owner)
    {
        // The caller serializes this owner's requests, so all its earlier grants have
        // completed and none can arrive during the mark. Other owners may advance the
        // sequence concurrently; any atomic value between their grants is a valid boundary
        // for this owner. No lock-table inspection or manager-wide lock is needed.
        return new LockStatementMark(this, owner, Interlocked.Read(ref _grantSequence));
    }

    /// <summary>
    /// Releases resources first granted to the owner after a statement mark and wakes their
    /// queues, without ending the owner or releasing any resource held at the mark.
    /// </summary>
    /// <param name="mark">The mark created before the statement acquired locks.</param>
    /// <exception cref="ArgumentException">The mark is default or belongs to another manager.</exception>
    /// <remarks>
    /// The caller must have no surviving writes since the mark and no pending lock requests:
    /// it has not applied, or its failed statement bracket has been fully rolled back physically.
    /// This operation performs no undo. Re-grants and upgrades of a resource held at the mark are
    /// retained in full; no downgrade occurs. An abandoned owner keeps all grants until its deferred
    /// undo completes. In engine mode surviving writes retain transaction-duration locking. To retain a completed arbitration wait's row across
    /// restarts, take a new mark after its grant. Use marks within the same transaction lifetime.
    /// </remarks>
    public void ReleaseSince(LockStatementMark mark)
    {
        if (!ReferenceEquals(mark.Manager, this))
        {
            throw new ArgumentException(ErrorMessages.LockStatementMarkInvalid, nameof(mark));
        }

        List<Waiter>? granted = null;
        List<Waiter>? victims = null;
        lock (_sync)
        {
            if (!_abandoned.Contains(mark.Owner.Value) && _owned.TryGetValue(mark.Owner.Value, out var resources))
            {
                foreach (var resource in resources)
                {
                    if (_table.TryGetValue(resource, out var entry) &&
                        entry.Granted.TryGetValue(mark.Owner.Value, out var grant) && grant.Sequence > mark.GrantSequence)
                    {
                        entry.Granted.Remove(mark.Owner.Value);
                        LeftLocked(entry);
                    }
                }
            }

            WakeScheduledLocked(ref granted, ref victims);
        }

        CompleteGranted(granted);
        FailDeadlocked(victims);
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

            // Only the owner's entries can change: a request waits only for the grants and the
            // requests queued ahead of it on its own entry, so the owner's grants and requests
            // block nobody anywhere else.
            if (_owned.Remove(owner.Value, out var resources))
            {
                foreach (var resource in resources)
                {
                    if (_table.TryGetValue(resource, out var entry) && entry.Granted.Remove(owner.Value))
                    {
                        LeftLocked(entry);
                    }
                }
            }

            // Wake the requests queued there in FIFO order.
            WakeScheduledLocked(ref granted, ref victims);
        }

        CompleteGranted(granted);
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
    /// manager releases them. A request queued behind a removed one may then be granted.
    /// <para>
    /// The owner stays abandoned until <see cref="ReleaseAll"/>: a request it makes in the
    /// meantime (a late operation of the ended transaction) is refused unless the modes the
    /// owner already holds on the resource cover it, instead of queuing. A queued request
    /// would join the wait-for graph, where a live transaction could be chosen as the deadlock
    /// victim of a transaction that has already ended, and a new grant would stay held until
    /// the deferred undo completes, because only the manager releases a tracked transaction's
    /// locks.
    /// </para>
    /// </remarks>
    internal void AbandonPending(TransactionSequence owner)
    {
        List<Waiter>? granted = null;
        List<Waiter>? ended = null;
        List<Waiter>? victims = null;

        lock (_sync)
        {
            _abandoned.Add(owner.Value);
            DequeueOwnerLocked(owner.Value, ref ended);
            WakeScheduledLocked(ref granted, ref victims);
        }

        CompleteGranted(granted);
        FailEnded(owner, ended);
        FailDeadlocked(victims);
    }

    private void CompleteGranted(List<Waiter>? granted)
    {
        if (granted is null)
        {
            return;
        }

        foreach (var waiter in granted)
        {
            BeforeGrantNotification?.Invoke();
            waiter.Completion.TrySetResult();
        }
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
                string.Format(System.Globalization.CultureInfo.CurrentCulture, _errorLockManagerFailEndedFailure, owner, waiter.Mode, waiter.Resource)));
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

    private void CancelWaiter(Waiter waiter, string outcome)
    {
        List<Waiter>? granted = null;
        List<Waiter>? victims = null;
        bool removed;

        lock (_sync)
        {
            // A request already granted, failed or canceled has left its queue. One that leaves
            // it now may unblock the requests queued behind it.
            if (removed = waiter.Entry.Waiters.Remove(waiter))
            {
                waiter.CancellationOutcome = outcome;
                UntrackWaitLocked(waiter);
                LeftLocked(waiter.Entry);
                WakeScheduledLocked(ref granted, ref victims);
            }
        }

        // Grant/end and cancellation arbitrate under _sync. Once a grant removed the waiter,
        // its completion owns the outcome even if its asynchronous notification has not run.
        if (removed)
        {
            if (outcome == WaitTimedOut)
            {
                waiter.Completion.TrySetException(new TransactionLockTimeoutException(new OperationCanceledException()));
            }
            else if (outcome == WaitAbandoned)
            {
                waiter.Completion.TrySetException(Abandoned(new TransactionSequence(waiter.Owner), waiter.Resource,
                    waiter.Mode, Volatile.Read(ref _abandonCause)!));
            }
            else { waiter.Completion.TrySetCanceled(waiter.CallerToken); }
        }
        CompleteGranted(granted);
        FailDeadlocked(victims);
    }

    /// <summary>
    /// Whether a request of an abandoned owner must be refused: everything except a request the
    /// modes the owner already holds on the resource cover, whose grant changes nothing for
    /// anyone else, because every mode it conflicts with already conflicts with one the owner
    /// holds (the engines' writer-lock helpers re-request the database lock their transaction
    /// holds, then see the end and give the grant back).
    /// </summary>
    private bool RefuseAbandonedLocked(LockEntry entry, ulong owner, LockMode mode)
        => _abandoned.Contains(owner)
            && !(entry.Granted.TryGetValue(owner, out var held) && (_conflicts[(int)mode] & ~_heldConflicts[held.Modes]) == 0);

    // A refused request, a dequeued waiter or a release may have left the entry with nothing in it.
    // An entry the wake list still holds is removed when the list is walked.
    private void RemoveIfUnusedLocked(LockEntry entry)
    {
        if (entry.Granted.Count == 0 && entry.Waiters.Count == 0 && !entry.WakeScheduled)
        {
            _table.Remove(entry.Resource);
        }
    }

    private LockEntry GetEntryLocked(LockResource resource)
    {
        if (!_table.TryGetValue(resource, out var entry))
        {
            entry = new LockEntry(resource);
            _table[resource] = entry;
        }

        return entry;
    }

    private static byte Bit(LockMode mode) => (byte)(1 << (int)mode);

    // Whether a request for the mode conflicts with the modes a mask holds.
    private static bool Conflicts(byte held, LockMode requested) => (held & _conflicts[(int)requested]) != 0;

    private static bool Conflicts(LockMode queued, LockMode requested) => (Bit(queued) & _conflicts[(int)requested]) != 0;

    private static byte[] BuildConflicts()
    {
        var conflicts = new byte[ModeCount];
        for (int held = 0; held < ModeCount; held++)
        {
            for (int requested = 0; requested < ModeCount; requested++)
            {
                if (!_compatible[held, requested])
                {
                    conflicts[held] |= (byte)(1 << requested);
                }
            }
        }

        return conflicts;
    }

    private static byte[] BuildHeldConflicts()
    {
        var conflicts = new byte[AllModes + 1];
        for (int mask = 1; mask <= AllModes; mask++)
        {
            for (int mode = 0; mode < ModeCount; mode++)
            {
                if ((mask & (1 << mode)) != 0)
                {
                    conflicts[mask] |= _conflicts[mode];
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Whether a new request can be granted now, and otherwise its place in the entry's queue
    /// (#1390 review). A request is granted when no other owner holds a mode it conflicts with
    /// and no request of another owner ahead of its place conflicts with it, so a stream of
    /// compatible grants cannot pass a waiting request for as long as the stream lasts. A request
    /// of an owner that holds nothing on the resource takes its place at the end of the queue. One
    /// of an owner that holds the resource takes it ahead of the first queued request that waits
    /// for the owner (one whose mode conflicts with a mode the owner holds), which cannot be
    /// granted before the owner ends anyway; so a re-grant, which a statement apply makes inside
    /// the coordinator's gate, is granted at once, since every request it conflicts with is behind
    /// that place. That is PostgreSQL's lock manager: <c>LockAcquireExtended</c> queues a request
    /// that conflicts with a queued one (<c>src/backend/storage/lmgr/lock.c:1136-1143</c>), and
    /// <c>JoinWaitQueue</c> places a holder's request ahead of the first waiter that waits for it
    /// and grants it there when nothing ahead conflicts (<c>proc.c:1264-1314</c>).
    /// </summary>
    /// <param name="entry">The resource's entry.</param>
    /// <param name="owner">The owner requesting the grant.</param>
    /// <param name="mode">The requested mode.</param>
    /// <param name="place">The index the request takes in the queue if it is not granted.</param>
    private static bool CanGrantNewLocked(LockEntry entry, ulong owner, LockMode mode, out int place)
    {
        byte held = 0;
        bool blocked = false;
        foreach (var (holder, modes) in entry.Granted)
        {
            if (holder == owner)
            {
                held = modes.Modes;
            }
            else if (Conflicts(modes.Modes, mode))
            {
                blocked = true;
            }
        }

        var waiters = entry.Waiters;
        place = waiters.Count;
        byte ahead = 0;
        for (int i = 0; i < waiters.Count; i++)
        {
            var waiter = waiters[i];
            if (waiter.Owner == owner)
            {
                continue;
            }

            if (Conflicts(held, waiter.Mode))
            {
                place = i;
                break;
            }

            ahead |= Bit(waiter.Mode);
        }

        return !blocked && (ahead & _conflicts[(int)mode]) == 0;
    }

    /// <summary>
    /// Whether a queued request can be granted now: no other owner holds a mode it conflicts with,
    /// and no request of another owner queued ahead of it conflicts with it, as PostgreSQL's
    /// <c>ProcLockWakeup</c> decides (<c>src/backend/storage/lmgr/proc.c:1828-1863</c>).
    /// </summary>
    /// <param name="entry">The resource's entry.</param>
    /// <param name="queued">The queued request.</param>
    /// <param name="ahead">A superset of the modes requested ahead of it, which saves the walk of the queue when none conflicts.</param>
    private static bool CanGrantQueuedLocked(LockEntry entry, Waiter queued, byte ahead)
    {
        foreach (var (holder, held) in entry.Granted)
        {
            if (holder != queued.Owner && Conflicts(held.Modes, queued.Mode))
            {
                return false;
            }
        }

        if ((ahead & _conflicts[(int)queued.Mode]) == 0)
        {
            return true;
        }

        foreach (var waiter in entry.Waiters)
        {
            if (ReferenceEquals(waiter, queued))
            {
                break;
            }

            if (waiter.Owner != queued.Owner && Conflicts(waiter.Mode, queued.Mode))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Grants the mode to the owner, which keeps every mode it held (#1390 review), and takes the
    /// request the grant answers off its queue. A first grant of the resource to the owner lists it
    /// among the owner's resources.
    /// </summary>
    /// <remarks>
    /// The table used to keep only the owner's strongest mode by a fixed order (IS, S, IX, U, X),
    /// which the five modes do not have: intent-exclusive then update kept update, which allows
    /// shared, and lost intent-exclusive's conflict with it, so another owner's shared request
    /// queued before the update had no wait-for edge although nothing could grant it, and
    /// intent-exclusive then shared let another owner's intent-exclusive through. PostgreSQL keeps
    /// the same mask per holder (<c>holdMask</c>, <c>src/backend/storage/lmgr/lock.c:1702-1709</c>).
    /// </remarks>
    /// <param name="entry">The resource's entry.</param>
    /// <param name="owner">The owner granted the mode.</param>
    /// <param name="mode">The mode granted.</param>
    /// <param name="queued">The owner's queued request this grant answers, which leaves its queue; null for a new request.</param>
    /// <param name="victims">Collects the queued requests the grant leaves on a wait-for cycle, failed by the caller outside the lock.</param>
    private void GrantLocked(LockEntry entry, ulong owner, LockMode mode, Waiter? queued, ref List<Waiter>? victims)
    {
        bool holder = entry.Granted.TryGetValue(owner, out var grant);
        byte held = grant.Modes;
        byte holds = (byte)(held | Bit(mode));
        if (holds != held)
        {
            entry.Granted[owner] = new LockGrant(holds, holder ? grant.Sequence : Interlocked.Increment(ref _grantSequence));
        }

        if (!holder)
        {
            TrackLocked(owner, entry.Resource);
        }

        if (queued is not null)
        {
            entry.Waiters.Remove(queued);
            UntrackWaitLocked(queued);
        }

        // A grant adds wait-for edges when it gives the owner a mode that another owner's request
        // queued here conflicts with and nothing the owner held did: a holder's request granted at
        // its place, ahead of requests behind it. The search a request runs when it queues cannot
        // see them, because they did not exist then. With the five modes and the queue's places,
        // each such request already waits for the owner through a request ahead of it (the
        // Transactions DESIGN.md, "Grants respect the queue"), so no grant closes a cycle; the
        // search stays as a check of that argument. It can find a cycle only through a request the
        // owner itself still has queued (an owner with no queued request is not waiting), so only
        // then does the grant search, from each of those requests.
        if (holds != held && _waiting.Count != 0 && _waiting.ContainsKey(owner) && AddsEdgeLocked(entry, owner, held, mode))
        {
            FailCyclesThroughLocked(owner, ref victims);
        }
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

    /// <summary>
    /// Queues a request: at its place in its entry's queue (<see cref="CanGrantNewLocked"/>), among
    /// its owner's queued requests (the detector's edges), and among its owner's resources (the
    /// release's walk).
    /// </summary>
    private void EnqueueLocked(Waiter waiter, int place)
    {
        waiter.Entry.Waiters.Insert(place, waiter);

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
    /// lock, and schedules each entry's wake-up.
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
            LeftLocked(waiter.Entry);
            (dequeued ??= new()).Add(waiter);
        }
    }

    /// <summary>
    /// After a grant or a queued request left the entry: schedules the wake-up of the requests
    /// still queued there, or removes the entry when nothing is left in it.
    /// </summary>
    private void LeftLocked(LockEntry entry)
    {
        if (entry.Waiters.Count != 0)
        {
            ScheduleWakeLocked(entry);
        }
        else
        {
            RemoveIfUnusedLocked(entry);
        }
    }

    private void ScheduleWakeLocked(LockEntry entry)
    {
        if (!entry.WakeScheduled)
        {
            entry.WakeScheduled = true;
            _wake.Add(entry);
        }
    }

    /// <summary>
    /// Walks the queue of every entry scheduled for a wake-up, which a walk can schedule again (a
    /// grant can fail a deadlock victim queued anywhere), until none is left. Each walk that
    /// schedules another grants or fails a request, so the list drains.
    /// </summary>
    private void WakeScheduledLocked(ref List<Waiter>? granted, ref List<Waiter>? victims)
    {
        var wake = _wake;
        while (wake.Count != 0)
        {
            var entry = wake[^1];
            wake.RemoveAt(wake.Count - 1);
            entry.WakeScheduled = false;
            WakeLocked(entry, ref granted, ref victims);
            RemoveIfUnusedLocked(entry);
        }
    }

    /// <summary>
    /// Grants, in queue order, every request queued on the entry that can be granted now
    /// (<see cref="CanGrantQueuedLocked"/>), as PostgreSQL's <c>ProcLockWakeup</c> does
    /// (<c>src/backend/storage/lmgr/proc.c:1828-1863</c>). A grant only adds a holder and takes a
    /// request off the queue behind the ones already passed over, so none of those becomes
    /// grantable by it; a deadlock victim the grant fails schedules its entry again.
    /// </summary>
    private void WakeLocked(LockEntry entry, ref List<Waiter>? granted, ref List<Waiter>? victims)
    {
        var waiters = entry.Waiters;
        byte ahead = 0;
        for (int i = 0; i < waiters.Count;)
        {
            var waiter = waiters[i];
            if (CanGrantQueuedLocked(entry, waiter, ahead))
            {
                // Takes the request off the queue, so the next one is at the same index.
                int orderVersion = entry.OrderVersion;
                GrantLocked(entry, waiter.Owner, waiter.Mode, waiter, ref victims);
                (granted ??= new()).Add(waiter);
                if (entry.OrderVersion != orderVersion) { i = 0; ahead = 0; }
            }
            else
            {
                ahead |= Bit(waiter.Mode);
                i++;
            }
        }
    }

    // Whether the grant of the mode to the owner, which held the modes in held before, gives
    // another owner's queued request on the entry a wait-for edge it did not have.
    private static bool AddsEdgeLocked(LockEntry entry, ulong owner, byte held, LockMode granted)
    {
        foreach (var waiter in entry.Waiters)
        {
            if (waiter.Owner != owner && Conflicts(granted, waiter.Mode) && !Conflicts(held, waiter.Mode))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// After a grant to an owner that still has requests queued: fails each of those requests from
    /// which the owner can reach itself in the wait-for graph. Failing them breaks every cycle the
    /// grant closed, because each such cycle leaves the owner through one of them.
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
                if (TryReorderQueuesLocked()) { return; }
                DequeueLocked(waiter);
                LeftLocked(waiter.Entry);
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
    /// holds, in a conflicting mode, a resource one of its queued requests is for, and for the
    /// owner of every conflicting request queued ahead of it
    /// (<see cref="CanGrantQueuedLocked"/>). PostgreSQL calls the second kind a soft edge and, before it
    /// aborts anyone, tries to rearrange the queue so that no cycle is left
    /// (<c>src/backend/storage/lmgr/deadlock.c:624-768</c>, <c>TopoSort</c>). This lock manager
    /// first tries consistent queue repair when a cycle is found; a hard held-lock
    /// cycle still aborts the requester. Ordinary acyclic queues preserve FIFO.
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

    // Called only after finding a cycle. Held-lock edges cannot be changed; queued
    // edges can. Build an owner DAG from hard edges, then retain FIFO edges greedily
    // whenever they do not close a cycle. A stable topological owner order supplies
    // consistent queue orders across every resource (one owner may wait on several).
    // No live queue changes until hard-edge acyclicity has been proved.
    private bool TryReorderQueuesLocked()
    {
        var blockers = new Dictionary<ulong, HashSet<ulong>>();
        foreach (var requests in _waiting.Values)
        {
            foreach (var waiter in requests)
            {
                if (!blockers.TryGetValue(waiter.Owner, out var dependencies))
                {
                    dependencies = new();
                    blockers.Add(waiter.Owner, dependencies);
                }
                foreach (var (holder, held) in waiter.Entry.Granted)
                {
                    if (holder != waiter.Owner && Conflicts(held.Modes, waiter.Mode))
                    {
                        dependencies.Add(holder);
                        if (!blockers.ContainsKey(holder)) { blockers.Add(holder, new()); }
                    }
                }
            }
        }
        if (OrderOwners(blockers) is null) { return false; }

        foreach (var entry in _table.Values)
        {
            var queue = entry.Waiters;
            for (int later = 1; later < queue.Count; later++)
            {
                var waiting = queue[later];
                var dependencies = blockers[waiting.Owner];
                for (int earlier = 0; earlier < later; earlier++)
                {
                    var ahead = queue[earlier];
                    if (ahead.Owner == waiting.Owner || !Conflicts(ahead.Mode, waiting.Mode) ||
                        dependencies.Contains(ahead.Owner)) { continue; }
                    if (!GraphReaches(blockers, ahead.Owner, waiting.Owner))
                    {
                        dependencies.Add(ahead.Owner);
                    }
                }
            }
        }
        var order = OrderOwners(blockers)!;
        foreach (var entry in _table.Values)
        {
            var queue = entry.Waiters;
            bool changed = false;
            // Stable insertion sort keeps each owner's own requests in original order.
            for (int index = 1; index < queue.Count; index++)
            {
                var waiter = queue[index];
                int place = index;
                while (place > 0 && order[queue[place - 1].Owner] > order[waiter.Owner])
                {
                    queue[place] = queue[place - 1];
                    place--;
                }
                if (place != index) { queue[place] = waiter; changed = true; }
            }
            if (changed) { entry.OrderVersion++; ScheduleWakeLocked(entry); }
        }
        return true;
    }

    private static Dictionary<ulong, int>? OrderOwners(Dictionary<ulong, HashSet<ulong>> blockers)
    {
        var remaining = new Dictionary<ulong, int>(blockers.Count);
        var ready = new Queue<ulong>();
        foreach (var (owner, dependencies) in blockers)
        {
            remaining.Add(owner, dependencies.Count);
            if (dependencies.Count == 0) { ready.Enqueue(owner); }
        }
        var order = new Dictionary<ulong, int>(blockers.Count);
        while (ready.TryDequeue(out ulong resolved))
        {
            order.Add(resolved, order.Count);
            foreach (var (owner, dependencies) in blockers)
            {
                if (dependencies.Contains(resolved) && --remaining[owner] == 0)
                {
                    ready.Enqueue(owner);
                }
            }
        }
        return order.Count == blockers.Count ? order : null;
    }

    private bool GraphReaches(Dictionary<ulong, HashSet<ulong>> blockers, ulong start, ulong target)
    {
        var frontier = _searchFrontier;
        var visited = _searchVisited;
        frontier.Clear(); visited.Clear();
        frontier.Push(start);
        try
        {
            while (frontier.TryPop(out ulong owner))
            {
                if (owner == target) { return true; }
                if (!visited.Add(owner)) { continue; }
                foreach (ulong dependency in blockers[owner]) { frontier.Push(dependency); }
            }
            return false;
        }
        finally { frontier.Clear(); visited.Clear(); }
    }

    // Pushes the owners a queued request waits for, its edges: those whose grants conflict with
    // it, and those of the conflicting requests queued ahead of it. The same rule as
    // CanGrantQueuedLocked, or a cycle could hide behind an edge it misses.
    private static void PushBlockersLocked(Waiter waiter, Stack<ulong> frontier)
    {
        var entry = waiter.Entry;
        foreach (var (holder, held) in entry.Granted)
        {
            if (holder != waiter.Owner && Conflicts(held.Modes, waiter.Mode))
            {
                frontier.Push(holder);
            }
        }

        foreach (var ahead in entry.Waiters)
        {
            if (ReferenceEquals(ahead, waiter))
            {
                break;
            }

            if (ahead.Owner != waiter.Owner && Conflicts(ahead.Mode, waiter.Mode))
            {
                frontier.Push(ahead.Owner);
            }
        }
    }

    /// <summary>
    /// Writes <c>LockWaitBlocked</c> for a request that is still queued: the transactions it waits
    /// for, with the modes each holds or the mode each has queued ahead of it, and the requests
    /// each of those has queued in turn, with their own blockers. Read under the lock, written
    /// after it.
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

    // The owners a request waits for (PushBlockersLocked): a holder as "<sequence>:<modes held>",
    // modes joined by '+'; the owner of a request queued ahead as "<sequence>:queued <mode>". At
    // the first level each is followed by what it waits for itself: "1:Exclusive waiting for Shared
    // on Entry:2 (2:Exclusive)". A cycle of two shows in full; each waiter of a longer chain
    // reports its own step. The report is bounded (MaxBlockersDescribed and the others), the rest
    // counted.
    private void DescribeBlockersLocked(Waiter waiter, StringBuilder description, int depth)
    {
        int limit = depth == 0 ? MaxBlockersDescribed : MaxNestedBlockersDescribed;
        int described = 0;
        int more = 0;

        foreach (var (holder, held) in waiter.Entry.Granted)
        {
            if (holder == waiter.Owner || !Conflicts(held.Modes, waiter.Mode))
            {
                continue;
            }

            if (described == limit)
            {
                more++;
                continue;
            }

            described++;
            AppendSeparator(description);
            description.Append(holder.ToString(CultureInfo.InvariantCulture)).Append(':');
            AppendModes(description, held.Modes);
            DescribeQueuedLocked(holder, description, depth);
        }

        foreach (var ahead in waiter.Entry.Waiters)
        {
            if (ReferenceEquals(ahead, waiter))
            {
                break;
            }

            if (ahead.Owner == waiter.Owner || !Conflicts(ahead.Mode, waiter.Mode))
            {
                continue;
            }

            if (described == limit)
            {
                more++;
                continue;
            }

            described++;
            AppendSeparator(description);
            description.Append(ahead.Owner.ToString(CultureInfo.InvariantCulture)).Append(":queued ").Append(ahead.Mode.ToString());
            DescribeQueuedLocked(ahead.Owner, description, depth);
        }

        AppendMore(description, more);
    }

    // At the first level, what a blocker has queued itself, each request with its own blockers.
    private void DescribeQueuedLocked(ulong blocker, StringBuilder description, int depth)
    {
        if (depth != 0 || !_waiting.TryGetValue(blocker, out var queued))
        {
            return;
        }

        int described = 0;
        foreach (var request in queued)
        {
            if (described == MaxQueuedDescribed)
            {
                break;
            }

            described++;
            description.Append(" waiting for ").Append(request.Mode.ToString()).Append(" on ")
                .Append(TransactionEventSource.DescribeResource(request.Resource)).Append(" (");
            DescribeBlockersLocked(request, description, depth + 1);
            description.Append(')');
        }

        if (queued.Count > described)
        {
            description.Append(" and ").Append((queued.Count - described).ToString(CultureInfo.InvariantCulture)).Append(" more requests");
        }
    }

    private static void AppendSeparator(StringBuilder description)
    {
        if (description.Length != 0 && description[^1] != '(')
        {
            description.Append(", ");
        }
    }

    private static void AppendMore(StringBuilder description, int more)
    {
        if (more != 0)
        {
            AppendSeparator(description);
            description.Append("... (+").Append(more.ToString(CultureInfo.InvariantCulture)).Append(" more)");
        }
    }

    private static void AppendModes(StringBuilder description, byte held)
    {
        bool first = true;
        for (int mode = 0; mode < ModeCount; mode++)
        {
            if ((held & (1 << mode)) == 0)
            {
                continue;
            }

            if (!first)
            {
                description.Append('+');
            }

            description.Append(((LockMode)mode).ToString());
            first = false;
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

    private readonly record struct LockGrant(byte Modes, ulong Sequence);

    private sealed class LockEntry
    {
        public LockEntry(LockResource resource)
        {
            Resource = resource;
        }

        public LockResource Resource { get; }

        // Each holder's granted modes, as a mask with bit (int)mode set for each mode granted.
        public Dictionary<ulong, LockGrant> Granted { get; } = new();

        // The queued requests, in the order they queued.
        public List<Waiter> Waiters { get; } = new();

        public int OrderVersion { get; set; }

        // Whether the entry is on the wake list (WakeScheduledLocked).
        public bool WakeScheduled { get; set; }
    }

    private sealed class LockTimeoutScope : IDisposable
    {
        private LockManager? _manager;
        private readonly TimeSpan _previous;

        internal LockTimeoutScope(LockManager manager, TimeSpan previous)
        {
            _manager = manager;
            _previous = previous;
        }

        public void Dispose()
        {
            var manager = Interlocked.Exchange(ref _manager, null);
            if (manager is not null) { manager._lockTimeout.Value = _previous; }
        }
    }

    private sealed class Waiter
    {
        private readonly LockManager _manager;

        public Waiter(LockManager manager, ulong owner, LockMode mode, LockEntry entry, CancellationToken callerToken)
        {
            _manager = manager;
            Owner = owner;
            Mode = mode;
            Entry = entry;
            CallerToken = callerToken;
        }

        public ulong Owner { get; }

        public LockMode Mode { get; }

        public LockResource Resource => Entry.Resource;

        // The entry the request is queued on. An entry with a queued request stays in the table,
        // so this is the table's entry for the resource for as long as the request is queued.
        public LockEntry Entry { get; }

        public CancellationToken CallerToken { get; }

        // Written only by the callback that removes this request under _sync, before completion.
        public string? CancellationOutcome { get; set; }

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // The wait's cancellation callback.
        public void Cancel(string outcome) => _manager.CancelWaiter(this, outcome);
    }
}
