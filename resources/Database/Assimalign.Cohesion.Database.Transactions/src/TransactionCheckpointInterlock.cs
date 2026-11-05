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
    private static long _nextPauseEpisode;
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
    private long _pauseEpisode;
    private bool _checkpointFailed;
    private bool _pauseFailed;
    private Action<StorageOfflineException>? _offlineHook;
    private CancellationTokenSource? _pauseCancellation;
    private CancellationTokenSource? _offlineCancellation;
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
            if (value > 0) { AttachOfflineHook(); }
            Volatile.Write(ref _journalSizeLimit, value);
            if (value == 0) { EndPause("Disabled"); }
        }
    }

    /// <summary>Gets whether this storage is waiting for a checkpoint before admitting new writes.</summary>
    /// <remarks>Readers that do not open a physical write bracket remain admitted.</remarks>
    public bool IsAdmissionPaused => Volatile.Read(ref _pause) is not null;

    /// <summary>Observes the journal threshold and requests a checkpoint and admission pause when it is reached.</summary>
    /// <returns>True while new writes must wait for a checkpoint.</returns>
    public bool ObserveAdmissionPause() => ObservePause() is not null;

    // The offline fence wakes every parked writer; shutdown instead disables the limit
    // so mandatory cleanup can drain before storage closes.
    private void CloseAdmission()
    {
        Volatile.Write(ref _closed, 1);
        CancellationTokenSource? cancellation;
        CancellationTokenSource? offline;
        lock (_state) { cancellation = _pauseCancellation; offline = _offlineCancellation; }
        cancellation?.Cancel();
        offline?.Cancel();
        EndPause("Closing");
    }

    private TransactionCheckpointInterlock(PhysicalStorage storage)
    {
        _storage = storage;
        _checkpoint = storage.Checkpoint;
    }

    private void AttachOfflineHook()
    {
        // Models finish composing their cross-storage and lock-wait fences before
        // the engine arms admission. A coordinator may have created this interlock
        // earlier, so composition belongs here rather than in its constructor.
        if (ReferenceEquals(_storage.OnOffline, _offlineHook) && _offlineHook is not null) { return; }
        var onOffline = _storage.OnOffline;
        _offlineHook = error =>
        {
            try { CloseAdmission(); }
            finally { onOffline?.Invoke(error); }
        };
        _storage.OnOffline = _offlineHook;
        if (_storage.IsOffline) { CloseAdmission(); }
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
    /// <param name="waitForAdmission">False to refuse a pause immediately, including a pause that begins during physical gate acquisition, when the caller must retain a monitor or metadata lease.</param>
    /// <param name="drain">True for mandatory cleanup that must finish despite an admission pause.</param>
    /// <returns>A scope that services a deferred checkpoint before releasing the gate.</returns>
    /// <exception cref="ArgumentException"><paramref name="bracketClass"/> is empty.</exception>
    /// <exception cref="OperationCanceledException">Acquisition was canceled.</exception>
    /// <exception cref="TransactionAdmissionCanceledException">Non-waiting admission was refused, or a pause wait was canceled.</exception>
    /// <exception cref="StorageOfflineException">The storage is offline.</exception>
    /// <exception cref="ObjectDisposedException">Admission has closed.</exception>
    /// <remarks>Dispose on the entering thread, after every bracket and page latch has ended.</remarks>
    public Lease Enter(string bracketClass, long owner = 0, CancellationToken cancellationToken = default,
        bool waitForAdmission = true, bool drain = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bracketClass);
        cancellationToken.ThrowIfCancellationRequested();
        int thread = Environment.CurrentManagedThreadId;
        if (_insideCoordinatedApply?.Invoke() == true) { return new Lease(this, thread, 0); }
        bool nested = Volatile.Read(ref _rawThread) == thread;
        if (!nested) { Wait(cancellationToken, bracketClass, owner, drain, waitForAdmission); }
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
            _checkpointFailed = false;
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
            lock (_state) { _pending = 1; _checkpointFailed = true; }
            ObservePause();
            throw;
        }
        // This sharp checkpoint covered requests received while it ran too.
        lock (_state) { _pending = 0; _busy = false; _retryRequested = false; _checkpointSucceeded = true; _suppressForeground = false; _checkpointFailed = false; }
        EndPause("CheckpointCompleted");
    }

    internal void Wait(CancellationToken cancellationToken = default, string bracketClass = "Maintenance", long owner = 0, bool drain = false,
        bool waitForAdmission = true)
    {
        ThrowIfRecursiveWait();
        if (!waitForAdmission && !drain) { WaitGuarded(cancellationToken, bracketClass, owner); return; }
        bool counted = false;
        while (true)
        {
            if (!drain) { WaitForPause(cancellationToken, bracketClass, owner, ref counted); }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (drain) { _gate.Wait(cancellationToken); }
                else if (!_gate.Wait(0)) { WaitUntilAvailable(cancellationToken); }
            }
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
                    if (ObservePause() is not null) { BackOut(); continue; }
                }
                SetHolder(bracketClass, owner);
                return;
            }
            catch { BackOut(); throw; }
        }
    }

    internal ValueTask WaitAsync(CancellationToken cancellationToken = default, string bracketClass = "Maintenance", long owner = 0, bool drain = false,
        bool waitForAdmission = true)
    {
        ThrowIfRecursiveWait();
        if (cancellationToken.IsCancellationRequested) { return ValueTask.FromCanceled(cancellationToken); }
        if (!waitForAdmission && !drain) { return WaitGuardedAsync(cancellationToken, bracketClass, owner); }
        if (!drain)
        {
            ThrowIfClosed();
            if (ObservePause() is not null) { return WaitPausedAsync(cancellationToken, bracketClass, owner); }
        }
        if (!drain && !_gate.Wait(0))
        { return CompleteWaitAsync(WaitUntilAvailableAsync(cancellationToken), bracketClass, owner, cancellationToken, drain); }
        Task waiting = drain ? _gate.WaitAsync(cancellationToken) : Task.CompletedTask;
        if (!waiting.IsCompletedSuccessfully) { return CompleteWaitAsync(waiting, bracketClass, owner, cancellationToken, drain); }
        if (TryAdmit(bracketClass, owner, drain)) { return default; }
        BackOut();
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
        BackOut();
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

    private CancellationToken OfflineCancellation()
    {
        lock (_state)
        { return _closed != 0 ? new CancellationToken(canceled: true) : (_offlineCancellation ??= new CancellationTokenSource()).Token; }
    }

    private void WaitUntilAvailable(CancellationToken token)
    {
        CancellationToken closing = OfflineCancellation();
        using var linked = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token, closing) : null;
        try { _gate.Wait(linked?.Token ?? closing); }
        catch (OperationCanceledException) { ThrowIfClosed(); token.ThrowIfCancellationRequested(); throw; }
    }

    private async Task WaitUntilAvailableAsync(CancellationToken token)
    {
        CancellationToken closing = OfflineCancellation();
        using var linked = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token, closing) : null;
        try { await _gate.WaitAsync(linked?.Token ?? closing).ConfigureAwait(false); }
        catch (OperationCanceledException) { ThrowIfClosed(); token.ThrowIfCancellationRequested(); throw; }
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
        catch { BackOut(); throw; }
    }

    private void WaitGuarded(CancellationToken token, string bracketClass, long owner)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfClosed();
        if (ObservePause() is not null) { RefuseAdmission(token, bracketClass, owner); }
        if (!_gate.Wait(0))
        {
            CancellationToken pausing = PauseCancellation();
            using var linked = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token, pausing) : null;
            try { _gate.Wait(linked?.Token ?? pausing); }
            catch (OperationCanceledException)
            {
                ThrowIfClosed();
                token.ThrowIfCancellationRequested();
                RefuseAdmission(token, bracketClass, owner);
                throw;
            }
        }
        if (TryAdmit(bracketClass, owner, drain: false)) { return; }
        BackOut();
        RefuseAdmission(token, bracketClass, owner);
    }

    private ValueTask WaitGuardedAsync(CancellationToken token, string bracketClass, long owner)
    {
        token.ThrowIfCancellationRequested();
        ThrowIfClosed();
        if (ObservePause() is not null) { RefuseAdmission(token, bracketClass, owner); }
        if (!_gate.Wait(0)) { return CompleteGuardedWaitAsync(token, bracketClass, owner); }
        if (TryAdmit(bracketClass, owner, drain: false)) { return default; }
        BackOut();
        RefuseAdmission(token, bracketClass, owner);
        return default;
    }

    private CancellationToken PauseCancellation()
    {
        lock (_state)
        {
            // Created only on physical contention; every guarded semaphore wait is
            // interrupted if admission pauses or closes before the permit arrives.
            return _pause is not null || _closed != 0 ? new CancellationToken(canceled: true)
                : (_pauseCancellation ??= new CancellationTokenSource()).Token;
        }
    }

    private async ValueTask CompleteGuardedWaitAsync(CancellationToken token, string bracketClass, long owner)
    {
        CancellationToken pausing = PauseCancellation();
        using var linked = token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token, pausing) : null;
        try { await _gate.WaitAsync(linked?.Token ?? pausing).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            ThrowIfClosed();
            token.ThrowIfCancellationRequested();
            RefuseAdmission(token, bracketClass, owner);
            throw;
        }
        if (TryAdmit(bracketClass, owner, drain: false)) { return; }
        BackOut();
        RefuseAdmission(token, bracketClass, owner);
    }

    private void RefuseAdmission(CancellationToken token, string bracketClass, long owner)
    {
        ThrowIfClosed();
        TransactionEventSource.Log.AdmissionWait(_storage, bracketClass, owner);
        throw new TransactionAdmissionCanceledException(token);
    }

    private void BackOut()
    {
        // Pair every back-out with TryCheckpoint's second look. A pending request
        // remains due and explicitly wakes the engine even though no holder runs it.
        lock (_state) { ReleaseUnderState(); }
        if (Volatile.Read(ref _pending) != 0) { _storage.OnCheckpointNeeded?.Invoke(); }
    }

    private TaskCompletionSource? ObservePause()
    {
        if (Volatile.Read(ref _closed) != 0) { return null; }
        var existing = Volatile.Read(ref _pause);
        if (existing is not null)
        {
            ReportPauseFailure();
            return existing;
        }
        long limit = JournalSizeLimit;
        // No new lock on the healthy fast path. The checkpoint's own record is
        // excluded, just as it is for EN-C1's size-due predicate.
        if (!AdmissionPauseEnabled || limit == 0 || _storage.JournalBytesSinceCheckpoint < limit / 2 + limit % 2) { return null; }
        string holder;
        string reason;
        long owner;
        long journalBytes;
        long episode;
        CancellationTokenSource? cancellation;
        lock (_state)
        {
            if (_pause is not null) { return _pause; }
            // A checkpoint may have completed between the advisory read and this monitor.
            journalBytes = _storage.JournalBytesSinceCheckpoint;
            if (_closed != 0 || !AdmissionPauseEnabled || JournalSizeLimit != limit ||
                journalBytes < limit / 2 + limit % 2) { return null; }
            existing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pauseStarted = Stopwatch.GetTimestamp();
            episode = _pauseEpisode = Interlocked.Increment(ref _nextPauseEpisode);
            _pending = 1;
            holder = _holder; owner = _owner;
            _pauseFailed = _checkpointFailed;
            reason = _checkpointFailed ? "CheckpointFailure" : _busy ? "BracketOpen" : "CheckpointPending";
            Volatile.Write(ref _pause, existing);
            TransactionEventSource.Log.AdmissionPauseOpened(_pauseStarted);
            cancellation = _pauseCancellation;
        }
        // Semaphore cancellation runs outside _state and wakes guarded physical
        // waiters. They refuse admission and unwind their guards; only unguarded
        // callers may subsequently wait on the pause task.
        cancellation?.Cancel();
        TransactionEventSource.Log.AdmissionPaused(_storage, reason, holder, owner, journalBytes, limit, episode);
        _storage.OnCheckpointNeeded?.Invoke();
        return existing;
    }

    private void ReportPauseFailure()
    {
        long episode;
        lock (_state)
        {
            if (_pause is null || !_checkpointFailed || _pauseFailed) { return; }
            _pauseFailed = true;
            episode = _pauseEpisode;
        }
        TransactionEventSource.Log.AdmissionPauseFailed(_storage, episode);
    }

    private void EndPause(string reason)
    {
        TaskCompletionSource? pause;
        long started;
        long episode;
        lock (_state)
        {
            pause = _pause;
            if (pause is null) { return; }
            started = _pauseStarted;
            episode = _pauseEpisode;
            TransactionEventSource.Log.AdmissionPauseClosed(started);
            Volatile.Write(ref _pause, null);
            // Waiters may still own registrations on the canceled managed notice.
            // Drop it rather than dispose a source a late waiter can still register.
            _pauseCancellation = null;
        }
        try { TransactionEventSource.Log.AdmissionResumed(_storage, reason, Stopwatch.GetTimestamp() - started, episode); }
        finally { pause.TrySetResult(); }
    }

    // Lock order: model grants -> data admission semaphore -> catalog _sync ->
    // independent catalog admission semaphore -> log monitor -> storage transaction/
    // header locks -> page/pool latches. _state never spans storage work. A caller
    // parked on the pause task may retain model grants, but owns neither admission semaphore, catalog
    // monitor, TruncateSnapshotGate, reclamation run lock, provisioner apply gate,
    // DDL metadata lease nor page latch. Guarded callers refuse a pause; their ordinary
    // physical semaphore wait is token-cancellable and interrupted when a pause starts.
    // A holder never waits for model grants. Undo bypasses only the pause (still
    // takes its storage's semaphore), so it can release grants needed by waiters.
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
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_gate.Wait(0)) { await WaitUntilAvailableAsync(cancellationToken).ConfigureAwait(false); }
            }
            catch (OperationCanceledException exception) when (ObservePause() is not null)
            {
                if (!counted) { TransactionEventSource.Log.AdmissionWait(_storage, bracketClass, owner); }
                throw new TransactionAdmissionCanceledException(cancellationToken, exception);
            }
            if (TryAdmit(bracketClass, owner, drain: false)) { return; }
            BackOut();
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
