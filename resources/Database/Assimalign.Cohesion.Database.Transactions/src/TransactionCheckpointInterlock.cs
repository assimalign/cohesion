using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using PhysicalStorage = Assimalign.Cohesion.Database.Storage.Storage;
using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>Serializes physical write brackets and sharp checkpoints on one storage.</summary>
/// <remarks>
/// Coordinated statements, catalog writes and reclamation share the same per-storage gate.
/// Acquire model locks before entering; no operation inside the gate may wait for model locks.
/// Catalog storages have independent gates and no logical writer anchor.
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
    private Func<bool>? _insideCoordinatedApply;
    private string _holder = "Admission";
    private long _owner;
    private int _rawThread;
    private int _rawDepth;
    private int _pending;
    private long _unattributed;
    private ExceptionDispatchInfo? _failure;

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
    public IDisposable Enter(string bracketClass, long owner = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bracketClass);
        cancellationToken.ThrowIfCancellationRequested();
        // Allocate before acquisition: an allocation failure must never orphan the gate.
        bool coordinated = _insideCoordinatedApply?.Invoke() == true;
        var lease = new Lease(coordinated ? null : this);
        if (coordinated) { return lease; }
        int thread = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref _rawThread) == thread)
        {
            Interlocked.Increment(ref _rawDepth);
            return lease;
        }
        Wait(cancellationToken, bracketClass, owner);
        _rawDepth = 1;
        Volatile.Write(ref _rawThread, thread);
        return lease;
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
        Interlocked.Exchange(ref _failure, null)?.Throw();
        if (!_gate.Wait(gateTimeout, cancellationToken))
        {
            // Gate release and this second look share _state: a holder that just ended
            // cannot leave a deferred request with nobody responsible for servicing it.
            lock (_state)
            {
                if (!_gate.Wait(0))
                {
                    Volatile.Write(ref _pending, 1);
                    if (TransactionEventSource.Log.CheckpointDeferred(_storage))
                    {
                        if (_holder == "Admission") { _unattributed++; }
                        else { TransactionEventSource.Log.CheckpointBlocked(_storage, _holder, _owner); }
                    }
                    return false;
                }
            }
        }
        try
        {
            SetHolder("Checkpoint", 0);
            Interlocked.Exchange(ref _pending, 0);
            _checkpoint();
            return true;
        }
        finally { Release(); }
    }

    internal void Checkpoint(CancellationToken cancellationToken)
    {
        Wait(cancellationToken, "Checkpoint");
        try
        {
            Interlocked.Exchange(ref _pending, 0);
            _checkpoint();
        }
        finally { Release(); }
    }

    internal void ConfigureCheckpoint(Action checkpoint, Func<bool> insideCoordinatedApply)
    {
        _checkpoint = checkpoint;
        _insideCoordinatedApply = insideCoordinatedApply;
    }

    internal void Wait(CancellationToken cancellationToken = default, string bracketClass = "Maintenance", long owner = 0)
    {
        ThrowIfRecursiveWait();
        _gate.Wait(cancellationToken);
        try { SetHolder(bracketClass, owner); }
        catch { _gate.Release(); throw; }
    }

    internal ValueTask WaitAsync(CancellationToken cancellationToken = default, string bracketClass = "Maintenance", long owner = 0)
    {
        ThrowIfRecursiveWait();
        Task waiting = _gate.WaitAsync(cancellationToken);
        if (!waiting.IsCompletedSuccessfully) { return CompleteWaitAsync(waiting, bracketClass, owner); }
        try { SetHolder(bracketClass, owner); }
        catch { _gate.Release(); throw; }
        return default;
    }

    private async ValueTask CompleteWaitAsync(Task waiting, string bracketClass, long owner)
    {
        await waiting.ConfigureAwait(false);
        try { SetHolder(bracketClass, owner); }
        catch { _gate.Release(); throw; }
    }

    private void ThrowIfRecursiveWait()
    {
        if (Volatile.Read(ref _rawThread) == Environment.CurrentManagedThreadId || _insideCoordinatedApply?.Invoke() == true)
        { throw new StorageTransactionException(ErrorMessages.CheckpointInterlockRecursiveWait); }
    }

    internal void SetHolder(string bracketClass, long owner)
    {
        lock (_state)
        {
            _holder = bracketClass;
            _owner = owner;
            // SemaphoreSlim may reserve its permit for an asynchronous waiter before
            // that waiter is scheduled. Attribute those deferrals when its admission
            // publishes the actual bracket class, rather than inventing a raw blocker.
            long deferred = _unattributed;
            _unattributed = 0;
            for (long i = 0; i < deferred; i++)
            { TransactionEventSource.Log.CheckpointBlocked(_storage, bracketClass, owner); }
        }
    }

    internal void Release()
    {
        lock (_state)
        {
            // Claim the request or release atomically with TryCheckpoint's second
            // look. No deferral can land after the final check but before release.
            if (Interlocked.Exchange(ref _pending, 0) == 0)
            {
                ReleaseUnderState();
                return;
            }
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
        _holder = "Admission";
        _owner = 0;
        _gate.Release();
    }

    private void RunDeferred()
    {
        try { _checkpoint(); }
        catch (StorageTransactionException exception) when (exception.Code != StorageTransactionErrorCode.ForeignStorage)
        {
            Volatile.Write(ref _pending, 1);
            TransactionEventSource.Log.DeferredCheckpointSkipped(_storage, "BracketOpen");
        }
        catch (StorageOfflineException)
        { TransactionEventSource.Log.DeferredCheckpointSkipped(_storage, "StorageOffline"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Volatile.Write(ref _failure, ExceptionDispatchInfo.Capture(exception));
            TransactionEventSource.Log.DeferredCheckpointFailed(_storage, exception);
        }
    }

    private void ExitRaw()
    {
        if (Interlocked.Decrement(ref _rawDepth) != 0) { return; }
        Volatile.Write(ref _rawThread, 0);
        Release();
    }

    [System.Diagnostics.DebuggerDisplay("Checkpoint scope")]
    [System.Diagnostics.DebuggerTypeProxy(typeof(Lease.DebugView))]
    private sealed class Lease : IDisposable
    {
        private TransactionCheckpointInterlock? _owner;
        internal Lease(TransactionCheckpointInterlock? owner) { _owner = owner; }
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitRaw();
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
