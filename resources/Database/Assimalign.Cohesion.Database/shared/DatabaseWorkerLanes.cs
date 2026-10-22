using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

// One database's work per lane, with typed outcomes collected by a later pump pass. Dedicated
// threads isolate blocked I/O from other databases and the application thread pool. Each worker
// admits MaximumLanes non-stuck operations under a hard ceiling of twice that many threads; excess work is deferred, never queued
// behind an indefinitely blocked device. Idle runners retire after IdleTimeout.
// Disposal joins active and parked runners before the owning engine closes any storage.
internal sealed class DatabaseWorkerLanes<TResult> : IDisposable
{
    /// <summary>How long a lane thread stays parked without a work before it ends.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the worker waits on a work before it looks again whether another database
    /// needs it, and the least it waits before it leaves a work for one.
    /// </summary>
    internal static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(5);

    private readonly string _threadName;
    private readonly int _maximumLanes;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _stuckThreshold;
    private readonly int _hardCeiling;
    private int _stuckRunning;
    internal Action<string, TimeSpan>? Stuck { get; set; }
    internal Action<string, TimeSpan>? DisposalWaiting { get; set; }
    internal TimeSpan DisposalReportInterval { get; set; } = TimeSpan.FromSeconds(1);
    internal Action? BeforeStart { get; set; }

    // Lock order: worker pass gate -> this monitor. Release this monitor before work
    // model/apply/storage locks, failure reporting, signal callbacks or pass publication.
    // A lane publishes its outcome under this monitor, then sets the signal after releasing it.
    private readonly object _sync = new();
    private readonly Dictionary<object, Lane> _lanes = new(ReferenceEqualityComparer.Instance);
    private readonly List<Runner> _idle = [];
    private int _running;
    private int _runners;
    internal const int MaximumLanes = 64;
    private long _pass;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseWorkerLanes{TResult}"/> class.
    /// </summary>
    /// <param name="threadName">The name of the lane threads.</param>
    /// <param name="maximumLanes">The normal admission cap; the hard thread ceiling is twice this.</param>
    /// <param name="idleTimeout">The idle retirement deadline; null uses the default.</param>
    /// <param name="stuckThreshold">The running time after which an operation releases its admission slot.</param>
    public DatabaseWorkerLanes(string threadName, int maximumLanes = MaximumLanes, TimeSpan? idleTimeout = null, TimeSpan? stuckThreshold = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLanes, 1);
        _threadName = threadName;
        _maximumLanes = maximumLanes;
        _hardCeiling = checked(maximumLanes * 2);
        _stuckThreshold = stuckThreshold ?? TimeSpan.FromSeconds(4);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_stuckThreshold, TimeSpan.Zero);
        _idleTimeout = idleTimeout ?? IdleTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_idleTimeout, TimeSpan.Zero);
    }

    /// <summary>
    /// Where a database's work stands on the lanes.
    /// </summary>
    internal enum LaneState
    {
        /// <summary>No work of the database is on a lane.</summary>
        Idle,

        /// <summary>A work the worker left runs on the database's lane.</summary>
        Running,

        /// <summary>A work the worker left ended; its outcome was collected.</summary>
        Ended,
    }

    /// <summary>
    /// The typed result of one database's work, or its failure.
    /// </summary>
    /// <param name="Result">The result, meaningful when Failure is null.</param>
    /// <param name="Failure">The exception the work threw, or null.</param>
    /// <param name="CancellationRequested">Whether the originating pass token was cancelled.</param>
    internal readonly record struct Outcome(TResult Result, Exception? Failure, bool CancellationRequested = false);

    /// <summary>
    /// Begins a pass of the worker: the lanes the pass collects or starts are the ones it visited.
    /// </summary>
    public void BeginPass()
    {
        List<(string Name, TimeSpan Elapsed)>? stuck = null;
        lock (_sync)
        {
            _pass++;
            foreach (var lane in _lanes.Values)
            {
                if (lane.Ended || lane.Stuck) { continue; }
                var elapsed = Stopwatch.GetElapsedTime(lane.Started);
                if (elapsed < _stuckThreshold) { continue; }
                lane.Stuck = true;
                _stuckRunning++;
                (stuck ??= []).Add((lane.Name, elapsed));
            }
        }
        // Reporting and listeners run outside the lane monitor, on the pump only.
        if (stuck is not null)
        {
            foreach (var item in stuck) { Stuck?.Invoke(item.Name, item.Elapsed); }
        }
    }

    /// <summary>
    /// Ends a pass of the worker: forgets the ended works of databases the pass did not visit,
    /// since a database that was dropped or closed is visited no more.
    /// </summary>
    public void EndPass()
    {
        lock (_sync)
        {
            List<object>? gone = null;
            foreach (var (database, lane) in _lanes)
            {
                if (lane.Ended && lane.Pass != _pass)
                {
                    (gone ??= []).Add(database);
                }
            }

            if (gone is not null)
            {
                foreach (object database in gone)
                {
                    _lanes.Remove(database);
                }
            }
        }
    }

    /// <summary>
    /// Collects the work the worker left on <paramref name="database"/>'s lane: reports whether
    /// one still runs, or hands back how it ended and forgets it.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="outcome">How the work ended, when it did.</param>
    /// <returns>Where the database's work stands.</returns>
    public LaneState Collect(object database, out Outcome outcome)
    {
        outcome = default;
        lock (_sync)
        {
            if (!_lanes.TryGetValue(database, out var lane))
            {
                return LaneState.Idle;
            }

            lane.Pass = _pass;
            if (!lane.Ended)
            {
                return LaneState.Running;
            }

            _lanes.Remove(database);
            outcome = lane.Outcome;
            return LaneState.Ended;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> for <paramref name="database"/> on a lane, and waits for it
    /// until it ends, <paramref name="signal"/> is set after <see cref="WaitSlice"/>,
    /// <paramref name="timeout"/> passes, or <paramref name="cancellationToken"/> is signaled.
    /// </summary>
    /// <param name="database">The database; it has no work on a lane.</param>
    /// <param name="work">The work returning a model-local result.</param>
    /// <param name="signal">The engine's work signal: set when another database needs the worker; a work left running sets it when it ends.</param>
    /// <param name="timeout">The longest the worker waits.</param>
    /// <param name="cancellationToken">Cancels the wait; the work goes on.</param>
    /// <param name="outcome">How the work ended, when it did.</param>
    /// <param name="visit">The optional pass observation to receive wait requests and elapsed time.</param>
    /// <returns>True when the work ended; false when it was left running on its lane.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled; the work goes on alone.</exception>
    public bool Run(object database, Func<TResult> work, ManualResetEventSlim signal, TimeSpan timeout, CancellationToken cancellationToken, out Outcome outcome, DatabaseWorkerPass.Visit? visit = null, string? databaseName = null)
    {
        long visitStarted = visit is null ? 0 : Stopwatch.GetTimestamp();
        Lane lane;
        bool start = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsSaturatedLocked)
            {
                if (visit is not null) { visit.Reason = DatabaseWorkerPass.VisitReason.Capacity; visit.Attempted = false; }
                outcome = default;
                return false;
            }
            lane = new Lane(work, signal, cancellationToken, databaseName ?? database.ToString()!);
            lane.Pass = _pass;
            _lanes.Add(database, lane);
            _running++;
            if (_idle.Count > 0)
            {
                var runner = _idle[^1];
                _idle.RemoveAt(_idle.Count - 1);
                runner.Next = lane;
                Monitor.PulseAll(_sync);
            }
            else
            {
                _runners++;
                start = true;
            }
        }

        if (start)
        {
            StartRunner(database, lane);
        }

        long started = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            while (!lane.Ended && !cancellationToken.IsCancellationRequested)
            {
                WaitForLane(WaitSlice, visit);
                var waited = Stopwatch.GetElapsedTime(started);
                if (lane.Ended || waited >= timeout || (waited >= WaitSlice && signal.IsSet))
                {
                    break;
                }
            }

            if (visit is not null) { visit.LaneWait = Stopwatch.GetElapsedTime(visitStarted); }

            if (lane.Ended)
            {
                _lanes.Remove(database);
                outcome = lane.Outcome;
                return true;
            }

            lane.Detached = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        outcome = default;
        return false;
    }

    // The caller holds _sync. Keep the requested delay and the blocking operation together,
    // including early wakeups: elapsed time and the number of requests are separate signals.
    private void WaitForLane(TimeSpan timeout, DatabaseWorkerPass.Visit? visit)
    {
        if (visit is not null)
        {
            visit.LaneWaits++;
            visit.LaneWaitRequested += timeout;
        }
        Monitor.Wait(_sync, timeout);
    }

    /// <summary>
    /// Stops the lanes: waits for every work still running, the ones the worker left
    /// included, so none outlives the engine's storages, and ends the lane threads.
    /// </summary>
    /// <remarks>
    /// A work whose device never answers holds this as long, as it held the worker's own
    /// thread, and with it the engine's disposal, before the lanes.
    /// </remarks>
    public void Dispose()
    {
        long started = Stopwatch.GetTimestamp();
        long lastReported = started;
        lock (_sync) { _disposed = true; Monitor.PulseAll(_sync); }
        while (true)
        {
            List<string>? waiting = null;
            lock (_sync)
            {
                if (_runners == 0) { _idle.Clear(); return; }
                Monitor.Wait(_sync, DisposalReportInterval);
                if (_runners == 0) { _idle.Clear(); return; }
                if (Stopwatch.GetElapsedTime(lastReported) >= DisposalReportInterval)
                {
                    lastReported = Stopwatch.GetTimestamp();
                    foreach (var lane in _lanes.Values)
                    {
                        if (!lane.Ended) { (waiting ??= []).Add(lane.Name); }
                    }
                }
            }
            if (waiting is not null)
            {
                foreach (string database in waiting) { DisposalWaiting?.Invoke(database, Stopwatch.GetElapsedTime(started)); }
            }
        }
    }

    internal int RunnerCount { get { lock (_sync) { return _runners; } } }

    private bool IsSaturatedLocked => _running - _stuckRunning >= _maximumLanes
        || (_idle.Count == 0 && _runners >= _hardCeiling);

    internal bool IsSaturated { get { lock (_sync) { return IsSaturatedLocked; } } }

    internal TimeSpan LimitWait(TimeSpan timeout)
    {
        lock (_sync)
        {
            foreach (var lane in _lanes.Values)
            {
                if (lane.Ended || lane.Stuck) { continue; }
                var remaining = _stuckThreshold - Stopwatch.GetElapsedTime(lane.Started);
                if (remaining <= TimeSpan.Zero) { return TimeSpan.Zero; }
                if (remaining < timeout) { timeout = remaining; }
            }
            return timeout;
        }
    }

    internal bool HasLane(object database) { lock (_sync) { return _lanes.ContainsKey(database); } }

    internal bool IsRunning(object database)
    {
        lock (_sync) { return _lanes.TryGetValue(database, out var lane) && !lane.Ended; }
    }

    // Test-only completion wait: does not collect outcomes or alter the worker's schedule.
    internal bool WaitForIdle(TimeSpan timeout)
    {
        long started = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            while (_running > 0)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) { return false; }
                Monitor.Wait(_sync, remaining);
            }
            return true;
        }
    }

    private void StartRunner(object database, Lane lane)
    {
        try
        {
            BeforeStart?.Invoke();
            // An explicit pass may run inside a statement. A lane must never inherit its
            // apply-gate reentrancy flag, security principal or other caller AsyncLocal state.
            if (ExecutionContext.IsFlowSuppressed()) { StartThread(lane); }
            else { using (ExecutionContext.SuppressFlow()) { StartThread(lane); } }
        }
        catch
        {
            lock (_sync)
            {
                _lanes.Remove(database);
                _running--;
                _runners--;
                Monitor.PulseAll(_sync);
            }

            throw;
        }
    }

    private void StartThread(Lane lane)
    {
        var thread = new Thread(() => RunLanes(new Runner(), lane)) { IsBackground = true, Name = _threadName };
        thread.Start();
    }

    // A lane thread: runs the work it was started with, then whatever the worker hands it
    // while it is parked, until it parks for IdleTimeout or the lanes are disposed.
    private void RunLanes(Runner runner, Lane lane)
    {
        try
        {
            Lane? next = lane;
            while (next is not null)
            {
                Execute(next, runner);
                next = Park(runner);
            }
        }
        finally
        {
            lock (_sync)
            {
                _runners--;
                Monitor.PulseAll(_sync);
            }
        }
    }

    private void Execute(Lane lane, Runner runner)
    {
        Exception? failure = null;
        try
        {
            // Restore the clean runner context after each job as well: a callback's ambient
            // state must not escape to another database when this dedicated runner is reused.
            ExecutionContext.Run(ExecutionContext.Capture()!, static state => ((Lane)state!).Run(), lane);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
        }

        bool detached;
        lock (_sync)
        {
            lane.Outcome = new Outcome(lane.Result, failure, lane.CancellationToken.IsCancellationRequested);
            lane.Ended = true;
            detached = lane.Detached;
            _running--;
            if (lane.Stuck) { _stuckRunning--; }
            // Publish availability with Ended. A collecting pass can reuse this runner immediately.
            runner.Next = null;
            if (!_disposed) { _idle.Add(runner); }
            Monitor.PulseAll(_sync);
        }

        if (detached)
        {
            try
            {
                // The worker left this work: the next pass collects it at once.
                lane.Signal.Set();
            }
            catch (ObjectDisposedException)
            {
                // The engine is gone; nothing is left to collect.
            }
        }
    }

    private Lane? Park(Runner runner)
    {
        lock (_sync)
        {
            long parked = Stopwatch.GetTimestamp();
            while (runner.Next is null)
            {
                var remaining = _idleTimeout - Stopwatch.GetElapsedTime(parked);
                if (_disposed || remaining <= TimeSpan.Zero)
                {
                    _idle.Remove(runner);
                    return null;
                }
                Monitor.Wait(_sync, remaining);
            }

            return runner.Next;
        }
    }

    /// <summary>One work on a lane.</summary>
    private sealed class Lane
    {
        public Lane(Func<TResult> work, ManualResetEventSlim signal, CancellationToken cancellationToken, string name)
        {
            Work = work;
            Name = name;
            Started = Stopwatch.GetTimestamp();
            Signal = signal;
            CancellationToken = cancellationToken;
        }

        public string Name { get; }
        public long Started { get; }
        public bool Stuck { get; set; }
        public Func<TResult> Work { get; }

        public TResult Result { get; private set; } = default!;

        public void Run() => Result = Work();

        public ManualResetEventSlim Signal { get; }

        public CancellationToken CancellationToken { get; }

        // Guarded by the lanes' lock.
        public Outcome Outcome { get; set; }

        public bool Ended { get; set; }

        public bool Detached { get; set; }

        public long Pass { get; set; }
    }

    /// <summary>A lane thread's parking slot: the work the worker handed it, if any.</summary>
    private sealed class Runner
    {
        // Guarded by the lanes' lock.
        public Lane? Next { get; set; }
    }
}
