using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

// Owns lane lifetime and opt-in pass observation for every built-in lane worker. Only the pump
// publishes observations and settles outcomes; lane threads never report to DatabaseEngineWorker.
internal abstract class DatabaseLaneWorker<TResult> : DatabaseEngineWorker
{
    private readonly ManualResetEventSlim _wake = new();
    protected DatabaseWorkerLanes<TResult> Lanes { get; }

    protected DatabaseLaneWorker(string name, DatabaseEngineWorkerKind kind, TimeSpan interval)
        : base(name, kind, interval)
    {
        // Four polling/maintenance intervals, bounded to [1 s, 4 s]. A slow device keeps
        // ownership of its I/O but releases admission capacity after this deadline.
        var threshold = TimeSpan.FromSeconds(Math.Clamp(interval.TotalSeconds * 4, 1, 4));
        Lanes = new DatabaseWorkerLanes<TResult>(name + " lane", stuckThreshold: threshold);
        Lanes.Stuck = (database, elapsed) => ReportLaneDelay(database, "stuck", elapsed);
        Lanes.DisposalWaiting = ReportLaneDisposalWait;
    }

    private int _triggerThread;
    private long _nextWriteBack;
    protected bool CompletionOnly { get; private set; }

    protected virtual ManualResetEventSlim LaneSignal => _wake;
    protected virtual TimeSpan TriggerTimeout => Kind != DatabaseEngineWorkerKind.PageWriteBack || _nextWriteBack == 0
        ? Interval : TimeSpan.FromSeconds(Math.Max(0, (_nextWriteBack - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));
    protected virtual TimeSpan LaneTimeout => DatabaseWorkerLanes<TResult>.WaitSlice;
    protected abstract void RunLanePass(CancellationToken cancellationToken, DatabaseWorkerPass? pass);

    protected void ReportCapacity(string database)
        => ReportLaneDelay(database, "capacity", TimeSpan.Zero);

    internal bool WaitForLanes(TimeSpan timeout) => Lanes.WaitForIdle(timeout);

    protected override ValueTask DisposeAsyncCore()
    {
        Lanes.Dispose();
        _pumpWait?.Dispose();
        _wake.Dispose();
        return ValueTask.CompletedTask;
    }

    private long _observedPass;
    private ThreadLocal<PumpWait>? _pumpWait;
    private Action<DatabaseWorkerPass>? _passCompleted;
    private Exception? _observerFailure;

    internal bool PumpBackoffObserved { get; set; }
    internal Exception? PassObserverFailure => Volatile.Read(ref _observerFailure);

    // Opt-in: no records or retained history without a listener. Called after lane cleanup,
    // outside lane/failure locks. This describes visits, before root failure settlement.
    // Listeners must not re-enter the worker. Callback failures are isolated and retained.
    internal Action<DatabaseWorkerPass>? PassCompleted
    {
        get => Volatile.Read(ref _passCompleted);
        set
        {
            if (value is not null && _pumpWait is null)
            {
                var waits = new ThreadLocal<PumpWait>(static () => new PumpWait());
                if (Interlocked.CompareExchange(ref _pumpWait, waits, null) is not null) { waits.Dispose(); }
            }
            Volatile.Write(ref _passCompleted, value);
        }
    }

    internal void RecordPumpBackoff(TimeSpan duration)
    {
        if (PassCompleted is not null) { _pumpWait!.Value!.Backoff += duration; }
    }

    internal void RecordTriggerElapsed(TimeSpan duration)
    {
        if (PassCompleted is not null)
        {
            var wait = _pumpWait!.Value!;
            wait.Elapsed = duration;
            wait.Measured = true;
        }
    }

    /// <inheritdoc />
    protected sealed override void WaitForTrigger(CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        bool signaled = false;
        var timeout = Lanes.LimitWait(TriggerTimeout);
        try
        {
            signaled = WaitForSignal(LaneSignal, timeout, cancellationToken);
            _triggerThread = Environment.CurrentManagedThreadId;
        }
        catch (OperationCanceledException)
        {
            // The pump is stopping; Run observes the token and exits.
        }
        catch (ObjectDisposedException)
        {
            // The engine is closing.
        }
        finally
        {
            var observation = PassCompleted is null ? null : _pumpWait!.Value;
            if (observation is not null)
            {
                observation.Timeout = timeout;
                observation.Elapsed = Stopwatch.GetElapsedTime(started);
                observation.Signaled = signaled;
            }
        }
    }

    // Keep each requested signal wait with its blocking operation. Additional pauses through
    // this path remain visible even when a signal wakes early or the runner is descheduled.
    private bool WaitForSignal(ManualResetEventSlim signal, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var observation = PassCompleted is null ? null : _pumpWait!.Value;
        if (observation is not null) { observation.Requests++; observation.Requested += timeout; }
        return signal.Wait(timeout, cancellationToken);
    }

    /// <inheritdoc />
    protected sealed override void RunIterationCore(CancellationToken cancellationToken)
    {
        bool fromPump = _triggerThread == Environment.CurrentManagedThreadId;
        long now = Stopwatch.GetTimestamp();
        CompletionOnly = Kind == DatabaseEngineWorkerKind.PageWriteBack && fromPump
            && _nextWriteBack != 0 && now < _nextWriteBack;
        if (fromPump && Kind == DatabaseEngineWorkerKind.PageWriteBack && !CompletionOnly)
        {
            _nextWriteBack = now + (long)(Interval.TotalSeconds * Stopwatch.Frequency);
        }
        var observer = PassCompleted;
        long started = observer is null ? 0 : Stopwatch.GetTimestamp();
        // Waits belong to their pump thread. A concurrent explicit RunIteration must not consume
        // the pump's pending wait or race a TimeSpan write. The base serializes pass publication;
        // lane threads never touch the record. Release occurs only after the pump has joined.
        var pumpWait = observer is null ? null : _pumpWait!.Value;
        var pass = observer is null ? null : new DatabaseWorkerPass(++_observedPass,
            PumpBackoffObserved ? pumpWait!.Backoff : null,
            pumpWait!.PassEnded == 0 ? null : Stopwatch.GetElapsedTime(pumpWait.PassEnded),
            pumpWait.Timeout, pumpWait.Elapsed, pumpWait.Signaled, pumpWait.Measured);
        if (pass is not null) { pass.TriggerWaits = pumpWait!.Requests; pass.TriggerWaitRequested = pumpWait.Requested; }
        pumpWait?.Reset();
        bool lanesBegun = false;
        try
        {
            // Lock order: root pass gate -> lane monitor; release the lane monitor before
            // model apply gates, storage/page locks, failure reports and observer callbacks.
            LaneSignal.Reset();
            Lanes.BeginPass();
            lanesBegun = true;
            RunLanePass(cancellationToken, pass);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (pass is not null) { pass.PassThrew = true; }
            throw;
        }
        finally
        {
            if (lanesBegun) { Lanes.EndPass(); }
            if (pass is not null)
            {
                pass.Elapsed = Stopwatch.GetElapsedTime(started);
                pass.Cancelled = cancellationToken.IsCancellationRequested;
                try { observer!(pass); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Test observers cannot change a pass's outcome or terminate its pump.
                    Volatile.Write(ref _observerFailure, exception);
                }
                pumpWait!.PassEnded = Stopwatch.GetTimestamp();
            }
        }
    }

    private sealed class PumpWait
    {
        internal int Requests;
        internal TimeSpan Requested;
        internal TimeSpan Backoff;
        internal TimeSpan Timeout;
        internal TimeSpan Elapsed;
        internal bool Signaled;
        internal bool Measured;
        internal long PassEnded;

        internal void Reset()
        {
            Requests = 0;
            Requested = Backoff = Timeout = Elapsed = TimeSpan.Zero;
            Signaled = false;
            Measured = false;
        }
    }

}
