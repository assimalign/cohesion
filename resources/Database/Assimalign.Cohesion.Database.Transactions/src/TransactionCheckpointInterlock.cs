using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using PhysicalStorage = Assimalign.Cohesion.Database.Storage.Storage;
using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>Provides engine plumbing to serialize physical write brackets and sharp checkpoints on one storage.</summary>
/// <remarks>
/// Coordinated statements, catalog writes and reclamation share the same per-storage gate.
/// Acquire model locks before entering; no operation inside the gate may wait for model locks.
/// Catalog storages have independent gates and no logical writer anchor.
/// An owner disables the pause before draining shutdown cleanup, and closes admission when storage is fenced offline.
/// </remarks>
[System.Diagnostics.DebuggerDisplay("TransactionCheckpointInterlock")]
[System.Diagnostics.DebuggerTypeProxy(typeof(TransactionCheckpointInterlock.DebugView))]
public sealed class TransactionCheckpointInterlock
{
    private static readonly ConditionalWeakTable<PhysicalStorage, TransactionCheckpointInterlock> _storages = new();
    private readonly PhysicalStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Gate -> state monitor. The reverse lookup uses Wait(0) only, never a
    // blocking gate wait under _state. State never spans checkpoint/storage/model
    // work; synchronous event observers must not reenter engine operations.
    private readonly object _state = new();
    private Action _checkpoint;
    private object? _binding;
    private Func<bool>? _insideCoordinatedApply;
    private string _holder = "Admission";
    private long _owner;
    private int _rawThread;
    private int _rawDepth;
    private int _pending;
    private bool _busy;
    private bool _checkpointSucceeded;
    private bool _retryRequested;
    private readonly HashSet<long> _leases = new();
    private long _nextLease;
    private long _outerIdentity;
    private long _unattributed;
    private ExceptionDispatchInfo? _failure;
    private bool _suppressForeground;
    private long _journalSizeLimit;
    private TaskCompletionSource? _pause;
    private long _pauseStarted;
    private int _closed;

    // Tests compare the same workload with and without the T3-27 backstop. The
    // size-due release checkpoint (EN-C1) is deliberately unaffected.
    internal bool AdmissionPauseEnabled { get; set; } = true;

    /// <summary>Gets or sets the journal limit whose halfway point pauses new write admission. Zero disables the backstop.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The limit is negative.</exception>
    public long JournalSizeLimit
    {
        get => Volatile.Read(ref _journalSizeLimit);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref _journalSizeLimit, value);
            if (value == 0) { EndPause("Disabled"); }
        }
    }

    /// <summary>Gets whether this storage is waiting for a checkpoint before admitting new writes.</summary>
    /// <remarks>Readers that do not open a physical write bracket remain admitted.</remarks>
    public bool IsAdmissionPaused => ObservePause() is not null;

    /// <summary>Ends new write admission and wakes paused callers during database shutdown. Undo and checkpoint work can still drain.</summary>
    public void CloseAdmission()
    {
        Volatile.Write(ref _closed, 1);
        EndPause("Closing");
    }

    private TransactionCheckpointInterlock(PhysicalStorage storage)
    {
        _storage = storage;
        _checkpoint = storage.Checkpoint;
    }

    /// <summary>Gets the single checkpoint interlock for a storage instance.</summary>
    /// <param name="storage">The physical file set protected by the interlock.</param>
    /// <returns>The storage's interlock, shared with its transaction coordinator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    public static TransactionCheckpointInterlock ForStorage(PhysicalStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return _storages.GetValue(storage, static storage => new TransactionCheckpointInterlock(storage));
    }

    /// <summary>Enters a synchronous physical write scope; nested scopes on this thread share its gate.</summary>
    /// <param name="bracketClass">The operation class reported when a checkpoint is deferred.</param>
    /// <param name="owner">An optional logical writer or physical object identity.</param>
    /// <param name="cancellationToken">Cancels acquisition before the scope enters.</param>
    /// <returns>A scope that services a deferred checkpoint before releasing the gate.</returns>
    /// <exception cref="ArgumentException"><paramref name="bracketClass"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">Acquisition was canceled.</exception>
    /// <remarks>Dispose on the entering thread, after every bracket and page latch has ended.</remarks>
    public Lease Enter(string bracketClass, long owner = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bracketClass);
        cancellationToken.ThrowIfCancellationRequested();
        int thread = Environment.CurrentManagedThreadId;
        if (_insideCoordinatedApply?.Invoke() == true) { return new Lease(this, thread, 0); }
        bool nested = Volatile.Read(ref _rawThread) == thread;
        if (!nested) { Wait(cancellationToken, bracketClass, owner); }
        try
        {
            long identity = ++_nextLease;
            _leases.Add(identity);
            if (_rawDepth == 0) { Volatile.Write(ref _outerIdentity, identity); }
            _rawDepth++;
            Volatile.Write(ref _rawThread, thread);
            return new Lease(this, thread, identity);
        }
        catch
        {
            if (!nested) { Release(); }
            throw;
        }
    }

    /// <summary>Runs a checkpoint, or transfers it to the scope currently holding the gate.</summary>
    /// <param name="gateTimeout">The maximum gate wait, or zero for an immediate attempt.</param>
    /// <param name="cancellationToken">Cancels the gate wait.</param>
    /// <returns>True when this call completed the checkpoint; false when a holder will run it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gateTimeout"/> is invalid.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    /// <exception cref="StorageTransactionException">Called from this thread's own synchronous scope, or a raw bracket is active.</exception>
    /// <exception cref="StorageOfflineException">The storage is offline or a durable checkpoint fails.</exception>
    /// <exception cref="Exception">The current checkpoint, or an earlier deferred checkpoint, failed.</exception>
    /// <remarks>A deferred operational failure is rethrown by the next call, preserving its identity.</remarks>
    public bool TryCheckpoint(TimeSpan gateTimeout, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _rawThread) == Environment.CurrentManagedThreadId || _insideCoordinatedApply?.Invoke() == true)
        { throw new StorageTransactionException(ErrorMessages.CheckpointInterlockRecursiveWait); }
        ObservePause();
        ExceptionDispatchInfo? failure;
        lock (_state)
        {
            failure = _failure;
            if (failure is not null) { _failure = null; _suppressForeground = false; }
        }
        failure?.Throw();
        if (!_gate.Wait(gateTimeout, cancellationToken))
        {
            // Gate release and this second look share _state: a holder that just ended
            // cannot leave a deferred request with nobody responsible for servicing it.
            bool deferred;
            string holder;
            long owner;
            // Snapshot only under state; synchronous listeners may block or reenter.
            bool listening = TransactionEventSource.Log.IsEnabled(System.Diagnostics.Tracing.EventLevel.Verbose,
                TransactionEventSource.Keywords.Checkpoints);
            lock (_state)
            {
                deferred = !_gate.Wait(0);
                holder = _holder;
                owner = _owner;
                if (deferred)
                {
                    _pending = 1;
                    _retryRequested = true;
                    if (listening && holder == "Admission") { _unattributed++; }
                }
            }
            if (deferred)
            {
                TransactionEventSource.Log.CheckpointDeferred(_storage);
                if (holder != "Admission") { TransactionEventSource.Log.CheckpointBlocked(_storage, holder, owner); }
                return false;
            }
        }
        try
        {
            SetHolder("Checkpoint", 0);
            Interlocked.Exchange(ref _pending, 0);
            RunCheckpoint();
            return true;
        }
        finally { Release(allowCheckpoint: false); }
    }

    internal void Checkpoint(CancellationToken cancellationToken)
    {
        ObservePause();
        Wait(cancellationToken, "Checkpoint", drain: true);
        try
        {
            Interlocked.Exchange(ref _pending, 0);
            RunCheckpoint();
        }
        finally { Release(allowCheckpoint: false); }
    }

    internal void ConfigureCheckpoint(object binding, Action checkpoint, Func<bool> insideCoordinatedApply)
    {
        lock (_state)
        {
            if (_binding is not null)
            { throw new InvalidOperationException(ErrorMessages.CheckpointInterlockAlreadyBound); }
            _binding = binding;
            _checkpoint = checkpoint;
            _insideCoordinatedApply = insideCoordinatedApply;
        }
    }

    internal void UnconfigureCheckpoint(object binding)
    {
        lock (_state)
        {
            if (!ReferenceEquals(_binding, binding)) { return; }
            _binding = null;
            _checkpoint = _storage.Checkpoint;
            _insideCoordinatedApply = null;
            _failure = null;
            _suppressForeground = false;
            _pending = 0;
            _busy = false;
            _retryRequested = false;
            _checkpointSucceeded = false;
            Volatile.Write(ref _journalSizeLimit, 0);
        }
        EndPause("Unconfigured");
    }

    private void RunCheckpoint()
    {
        lock (_state) { _pending = 0; _retryRequested = false; }
        try { _checkpoint(); }
        catch (StorageTransactionException exception) when (exception.Code != StorageTransactionErrorCode.ForeignStorage)
        {
            lock (_state) { _pending = 1; _busy = true; }
            throw;
        }
        catch
        {
            lock (_state) { _pending = 1; }
            throw;
        }
        // This sharp checkpoint covered requests received while it ran too.
        lock (_state) { _pending = 0; _busy = false; _retryRequested = false; _checkpointSucceeded = true; _suppressForeground = false; }
        EndPause("CheckpointCompleted");
    }

    internal void Wait(CancellationToken cancellationToken = default, string bracketClass = "Maintenance", long owner = 0, bool drain = false)
    {
        ThrowIfRecursiveWait();
        bool counted = false;
        while (true)
        {
            if (!drain) { WaitForPause(cancellationToken, bracketClass, owner, ref counted); }
            try { _gate.Wait(cancellationToken); }
            catch (OperationCanceledException exception) when (!drain && ObservePause() is not null)
            {
                if (!counted) { TransactionEventSource.Log.AdmissionWait(_storage, bracketClass, owner); }
                throw new TransactionAdmissionCanceledException(cancellationToken, exception);
            }
            try
            {
                if (!drain)
                {
                    ThrowIfClosed();
                    if (ObservePause() is not null) { _gate.Release(); continue; }
                }
                SetHolder(bracketClass, owner);
                return;
            }
            catch { _gate.Release(); throw; }
        }
    }

    internal ValueTask WaitAsync(CancellationToken cancellationToken = default, string bracketClass = "Maintenance", long owner = 0, bool drain = false)
    {
        ThrowIfRecursiveWait();
        if (!drain)
        {
            ThrowIfClosed();
            if (ObservePause() is not null) { return WaitPausedAsync(cancellationToken, bracketClass, owner); }
        }
        Task waiting = _gate.WaitAsync(cancellationToken);
        if (!waiting.IsCompletedSuccessfully) { return CompleteWaitAsync(waiting, bracketClass, owner, cancellationToken, drain); }
        if (TryAdmit(bracketClass, owner, drain)) { return default; }
        _gate.Release();
        return WaitPausedAsync(cancellationToken, bracketClass, owner);
    }

    private async ValueTask CompleteWaitAsync(Task waiting, string bracketClass, long owner, CancellationToken cancellationToken, bool drain)
    {
        try { await waiting.ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!drain && ObservePause() is not null)
        {
            TransactionEventSource.Log.AdmissionWait(_storage, bracketClass, owner);
            throw new TransactionAdmissionCanceledException(cancellationToken, exception);
        }
        if (TryAdmit(bracketClass, owner, drain)) { return; }
        _gate.Release();
        await WaitPausedAsync(cancellationToken, bracketClass, owner).ConfigureAwait(false);
    }

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closed) == 0) { return; }
        // Preserve the storage fence's identity at model translation boundaries;
        // a pause wake-up must not replace an offline refusal with disposal.
        if (_storage.OfflineError is { } offline) { throw offline; }
        ObjectDisposedException.ThrowIf(true, this);
    }

    private bool TryAdmit(string bracketClass, long owner, bool drain)
    {
        try
        {
            if (!drain)
            {
                ThrowIfClosed();
                if (ObservePause() is not null) { return false; }
            }
            SetHolder(bracketClass, owner);
            return true;
        }
        catch { _gate.Release(); throw; }
    }

    private TaskCompletionSource? ObservePause()
    {
        if (Volatile.Read(ref _closed) != 0) { return null; }
        var existing = Volatile.Read(ref _pause);
        if (existing is not null) { return existing; }
        long limit = JournalSizeLimit;
        // No new lock on the healthy fast path. The checkpoint's own record is
        // excluded, just as it is for EN-C1's size-due predicate.
        if (!AdmissionPauseEnabled || limit == 0 || _storage.JournalBytesSinceCheckpoint < limit / 2 + limit % 2
            || (Volatile.Read(ref _pending) == 0 && !_storage.IsCheckpointDue(TimeSpan.MaxValue))) { return null; }
        string holder;
        string reason;
        long owner;
        long journalBytes;
        lock (_state)
        {
            if (_pause is not null) { return _pause; }
            // A checkpoint may have completed between the advisory read and this monitor.
            journalBytes = _storage.JournalBytesSinceCheckpoint;
            if (_closed != 0 || !AdmissionPauseEnabled || JournalSizeLimit != limit ||
                journalBytes < limit / 2 + limit % 2) { return null; }
            existing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pauseStarted = Stopwatch.GetTimestamp();
            holder = _holder; owner = _owner;
            reason = _suppressForeground ? "CheckpointFailure" : _busy ? "BracketOpen" : "CheckpointPending";
            Volatile.Write(ref _pause, existing);
        }
        TransactionEventSource.Log.AdmissionPaused(_storage, reason, holder, owner, journalBytes, limit);
        return existing;
    }

    private void EndPause(string reason)
    {
        TaskCompletionSource? pause;
        long started;
        lock (_state)
        {
            pause = _pause;
            if (pause is null) { return; }
            started = _pauseStarted;
            Volatile.Write(ref _pause, null);
        }
        try { TransactionEventSource.Log.AdmissionResumed(_storage, reason, Stopwatch.GetTimestamp() - started); }
        finally { pause.TrySetResult(); }
    }

    // Lock order: model grants / catalog _sync -> admission semaphore -> log
    // monitor -> storage transaction/header locks -> page/pool latches. _state
    // snapshots never span storage work. A paused caller owns NO semaphore or
    // latch. A holder never waits for model grants. Undo bypasses only the pause
    // (still takes the semaphore), so it can release grants needed by waiters.
    // Nested raw/coordinated scopes reuse their outer admission. Checkpoints
    // bypass admission pausing; otherwise a checkpoint could wait for itself.
    private void WaitForPause(CancellationToken cancellationToken, string bracketClass, long owner, ref bool counted)
    {
        ThrowIfClosed();
        var pause = ObservePause();
        if (pause is null) { return; }
        if (!counted) { TransactionEventSource.Log.AdmissionWait(_storage, bracketClass, owner); counted = true; }
        try { pause.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult(); }
        catch (OperationCanceledException exception)
        { throw new TransactionAdmissionCanceledException(cancellationToken, exception); }
        ThrowIfClosed();
    }

    private async ValueTask WaitPausedAsync(CancellationToken cancellationToken, string bracketClass, long owner)
    {
        bool counted = false;
        while (true)
        {
            ThrowIfClosed();
            var pause = ObservePause();
            if (pause is not null)
            {
                if (!counted) { TransactionEventSource.Log.AdmissionWait(_storage, bracketClass, owner); counted = true; }
                try { await pause.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException exception)
                { throw new TransactionAdmissionCanceledException(cancellationToken, exception); }
            }
            ThrowIfClosed();
            try { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (ObservePause() is not null)
            {
                if (!counted) { TransactionEventSource.Log.AdmissionWait(_storage, bracketClass, owner); }
                throw new TransactionAdmissionCanceledException(cancellationToken, exception);
            }
            if (TryAdmit(bracketClass, owner, drain: false)) { return; }
            _gate.Release();
        }
    }

    private void ThrowIfRecursiveWait()
    {
        if (Volatile.Read(ref _rawThread) == Environment.CurrentManagedThreadId || _insideCoordinatedApply?.Invoke() == true)
        { throw new StorageTransactionException(ErrorMessages.CheckpointInterlockRecursiveWait); }
    }

    internal void SetHolder(string bracketClass, long owner)
    {
        long deferred;
        lock (_state)
        {
            _holder = bracketClass;
            _owner = owner;
            // SemaphoreSlim may reserve its permit for an asynchronous waiter before
            // that waiter is scheduled. Attribute those deferrals when its admission
            // publishes the actual bracket class, rather than inventing a raw blocker.
            deferred = _unattributed;
            _unattributed = 0;
        }
        for (long i = 0; i < deferred; i++)
        { TransactionEventSource.Log.CheckpointBlocked(_storage, bracketClass, owner); }
    }

    internal void Release(bool allowCheckpoint = true)
    {
        // MaxValue selects size only. InfiniteTimeSpan is negative and makes every
        // written journal interval-due. No storage work runs under the state monitor.
        bool due;
        // Our own brackets have ended while admission is still held. Only ungated
        // brackets remain, including any replacement for an earlier busy blocker.
        bool stillBlocked;
        try
        {
            if (allowCheckpoint) { ObservePause(); }
            due = allowCheckpoint && _storage.IsCheckpointDue(TimeSpan.MaxValue);
            stillBlocked = Volatile.Read(ref _busy) && _storage.HasActiveTransactions;
        }
        catch
        {
            // Even failure to allocate a pause task cannot strand the releasing
            // holder's permit after its brackets and latches have ended.
            lock (_state) { ReleaseUnderState(); }
            throw;
        }
        lock (_state)
        {
            // Claim the request or release atomically with TryCheckpoint's second
            // look. No deferral can land after the final check but before release.
            if (!allowCheckpoint || _suppressForeground || (_busy && !_retryRequested && stillBlocked) || (_pending == 0 && !due))
            {
                ReleaseUnderState();
                return;
            }
            _pending = 0;
            _retryRequested = false;
            // This holder already published its admission and queued attributions.
            _holder = "Checkpoint";
            _owner = 0;
        }
        try { RunDeferred(); }
        finally
        {
            lock (_state) { ReleaseUnderState(); }
        }
    }

    private void ReleaseUnderState()
    {
        if (_checkpointSucceeded)
        {
            _pending = 0;
            _busy = false;
            _retryRequested = false;
            _checkpointSucceeded = false;
        }
        _holder = "Admission";
        _owner = 0;
        _gate.Release();
    }

    private void RunDeferred()
    {
        try { RunCheckpoint(); }
        catch (StorageTransactionException exception) when (exception.Code != StorageTransactionErrorCode.ForeignStorage)
        {
            lock (_state) { _pending = 1; _busy = true; }
            TransactionEventSource.Log.DeferredCheckpointSkipped(_storage, "BracketOpen");
        }
        catch (StorageOfflineException)
        {
            lock (_state) { _pending = 0; _busy = false; }
            TransactionEventSource.Log.DeferredCheckpointSkipped(_storage, "StorageOffline");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Preserve the first uncollected identity across a successful explicit
            // checkpoint and a later failing cycle; every failure is still emitted.
            lock (_state) { _failure ??= ExceptionDispatchInfo.Capture(exception); _suppressForeground = true; }
            TransactionEventSource.Log.DeferredCheckpointFailed(_storage, exception);
        }
    }

    private void ExitRaw(int thread, long identity)
    {
        if (identity == 0 || identity < Volatile.Read(ref _outerIdentity) || Volatile.Read(ref _rawThread) != thread) { return; }
        if (thread != Environment.CurrentManagedThreadId)
        {
            TransactionEventSource.Log.CheckpointLeaseThreadMismatch(_storage, thread, Environment.CurrentManagedThreadId);
            Debug.Assert(thread == Environment.CurrentManagedThreadId, "Checkpoint leases must be disposed on their entering thread.");
            throw new StorageTransactionException(ErrorMessages.CheckpointInterlockWrongThread);
        }
        // Value copies and repeated Dispose calls share the identity, without an
        // allocation per lease. Capacity follows maximum nesting, not operation count.
        if (!_leases.Remove(identity)) { return; }
        if (--_rawDepth != 0) { return; }
        Volatile.Write(ref _rawThread, 0);
        Release();
    }

    /// <summary>A thread-affine engine write scope. Value copies share one disposal identity.</summary>
    [DebuggerDisplay("Checkpoint scope")]
    [DebuggerTypeProxy(typeof(Lease.DebugView))]
    public readonly struct Lease : IDisposable
    {
        private readonly TransactionCheckpointInterlock? _owner;
        private readonly int _thread;
        private readonly long _identity;
        internal Lease(TransactionCheckpointInterlock? owner, int thread, long identity)
        { _owner = owner; _thread = thread; _identity = identity; }
        /// <summary>Services due work and releases this scope on its entering thread.</summary>
        /// <remarks>Disposal is inert after a later outer scope re-enters, or when its entering thread holds no admission. Otherwise, cross-thread disposal is refused, including an ended nested copy within the same outer scope.</remarks>
        /// <exception cref="StorageTransactionException">The lease or a copy of it was disposed on another thread while its entering thread held admission.</exception>
        public void Dispose() => _owner?.ExitRaw(_thread, _identity);
        private sealed class DebugView
        {
            public DebugView(Lease lease) { }
        }
    }

    private sealed class DebugView
    {
        public DebugView(TransactionCheckpointInterlock interlock) { }
    }
}
