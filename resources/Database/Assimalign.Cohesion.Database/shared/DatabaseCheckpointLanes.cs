using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The lanes an engine's checkpoint worker runs its databases' checkpoints on: each checkpoint runs
/// on a dedicated lane thread, at most one per database at a time, so a checkpoint that hangs or
/// crawls in its device holds back its own database only.
/// </summary>
/// <remarks>
/// <para>
/// The worker hands a database's checkpoint to a lane and waits for it while nothing else needs
/// the worker, so a checkpoint that ends is settled by the pass that started it, as before the
/// lanes. The worker stops waiting when the engine's checkpoint signal is set (another database's
/// journal reached its size), when its poll timeout passes (a database due by time may be
/// waiting), or when the pass is cancelled. The checkpoint then goes on alone on its lane, the
/// passes that follow leave its database out until it ends, and the pass that finds it ended
/// collects its outcome. A checkpoint that ends after the worker left it sets the checkpoint signal,
/// so that pass comes at once.
/// </para>
/// <para>
/// One worker thread visiting the databases in turn let a single database's device stall every
/// other database's checkpoint for as long as it took to answer, or to fail. Neo4j gives each
/// database its own checkpoint job, which reschedules itself only once its run ended, so a slow
/// database never delays another's
/// (<c>community/kernel/src/main/java/org/neo4j/kernel/database/Database.java:1155-1157</c>,
/// <c>community/kernel/src/main/java/org/neo4j/wal/checkpoint/CheckPointScheduler.java:51-84</c>);
/// PostgreSQL runs one checkpointer per cluster, each with its own WAL
/// (<c>src/backend/postmaster/checkpointer.c:5</c>), which is what a database with its own journal
/// is here.
/// </para>
/// <para>
/// Lane threads are dedicated rather than the shared thread pool: a checkpoint blocks in durable
/// flushes, and the pool's queue must not delay it behind application work, as nothing delayed the
/// worker's own dedicated thread. A lane thread parks between checkpoints and ends after
/// <see cref="IdleTimeout"/> without one. There are as many as there are checkpoints running at
/// once, which is one unless the worker left a checkpoint running. Only an
/// <see cref="OutOfMemoryException"/> escapes a lane thread, which ends the process.
/// </para>
/// </remarks>
internal sealed class DatabaseCheckpointLanes : IDisposable
{
    /// <summary>How long a lane thread stays parked without a checkpoint before it ends.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the worker waits on a checkpoint before it looks again whether another database
    /// needs it, and the least it waits before it leaves a checkpoint for one.
    /// </summary>
    internal static readonly TimeSpan WaitSlice = TimeSpan.FromMilliseconds(5);

    private readonly string _threadName;

    // Guards everything below; never held while a checkpoint runs.
    private readonly object _sync = new();
    private readonly Dictionary<object, Lane> _lanes = new(ReferenceEqualityComparer.Instance);
    private readonly List<Runner> _idle = [];
    private int _running;
    private long _pass;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseCheckpointLanes"/> class.
    /// </summary>
    /// <param name="threadName">The name of the lane threads.</param>
    public DatabaseCheckpointLanes(string threadName)
    {
        _threadName = threadName;
    }

    /// <summary>
    /// Where a database's checkpoint stands on the lanes.
    /// </summary>
    internal enum LaneState
    {
        /// <summary>No checkpoint of the database is on a lane.</summary>
        Idle,

        /// <summary>A checkpoint the worker left runs on the database's lane.</summary>
        Running,

        /// <summary>A checkpoint the worker left ended; its outcome was collected.</summary>
        Ended,
    }

    /// <summary>
    /// How a checkpoint on a lane ended: finished, left to a later pass (<see cref="Finished"/>
    /// false, no failure: a statement took it over), or failed.
    /// </summary>
    /// <param name="Finished">True when the checkpoint finished.</param>
    /// <param name="Failure">The exception the checkpoint threw, or null.</param>
    internal readonly record struct Outcome(bool Finished, Exception? Failure);

    /// <summary>
    /// Begins a pass of the worker: the lanes the pass collects or starts are the ones it visited.
    /// </summary>
    public void BeginPass()
    {
        lock (_sync)
        {
            _pass++;
        }
    }

    /// <summary>
    /// Ends a pass of the worker: forgets the ended checkpoints of databases the pass did not visit,
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
    /// Collects the checkpoint the worker left on <paramref name="database"/>'s lane: reports whether
    /// one still runs, or hands back how it ended and forgets it.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="outcome">How the checkpoint ended, when it did.</param>
    /// <returns>Where the database's checkpoint stands.</returns>
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
    /// Runs <paramref name="checkpoint"/> for <paramref name="database"/> on a lane, and waits for it
    /// until it ends, <paramref name="signal"/> is set after <see cref="WaitSlice"/>,
    /// <paramref name="timeout"/> passes, or <paramref name="cancellationToken"/> is signaled.
    /// </summary>
    /// <param name="database">The database; it has no checkpoint on a lane.</param>
    /// <param name="checkpoint">The checkpoint: returns true when it finished, false when a statement took it over.</param>
    /// <param name="signal">The engine's checkpoint signal: set when another database needs the worker; a checkpoint left running sets it when it ends.</param>
    /// <param name="timeout">The longest the worker waits.</param>
    /// <param name="cancellationToken">Cancels the wait; the checkpoint goes on.</param>
    /// <param name="outcome">How the checkpoint ended, when it did.</param>
    /// <returns>True when the checkpoint ended; false when it was left running on its lane.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled; the checkpoint goes on alone.</exception>
    public bool Run(object database, Func<bool> checkpoint, ManualResetEventSlim signal, TimeSpan timeout, CancellationToken cancellationToken, out Outcome outcome)
    {
        var lane = new Lane(checkpoint, signal);
        bool start = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
                Monitor.Wait(_sync, WaitSlice);
                var waited = Stopwatch.GetElapsedTime(started);
                if (lane.Ended || waited >= timeout || (waited >= WaitSlice && signal.IsSet))
                {
                    break;
                }
            }

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

    /// <summary>
    /// Stops the lanes: waits for every checkpoint still running, the ones the worker left
    /// included, so none outlives the engine's storages, and ends the lane threads.
    /// </summary>
    /// <remarks>
    /// A checkpoint whose device never answers holds this as long, as it held the worker's own
    /// thread, and with it the engine's disposal, before the lanes.
    /// </remarks>
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            Monitor.PulseAll(_sync);
            while (_running > 0)
            {
                Monitor.Wait(_sync);
            }
        }
    }

    private void StartRunner(object database, Lane lane)
    {
        try
        {
            var thread = new Thread(() => RunLanes(new Runner(), lane)) { IsBackground = true, Name = _threadName };
            thread.Start();
        }
        catch
        {
            lock (_sync)
            {
                _lanes.Remove(database);
                _running--;
                Monitor.PulseAll(_sync);
            }

            throw;
        }
    }

    // A lane thread: runs the checkpoint it was started with, then whatever the worker hands it
    // while it is parked, until it parks for IdleTimeout or the lanes are disposed.
    private void RunLanes(Runner runner, Lane lane)
    {
        Lane? next = lane;
        while (next is not null)
        {
            Execute(next);
            next = Park(runner);
        }
    }

    private void Execute(Lane lane)
    {
        bool finished = false;
        Exception? failure = null;
        try
        {
            finished = lane.Checkpoint();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
        }

        bool detached;
        lock (_sync)
        {
            lane.Outcome = new Outcome(finished, failure);
            lane.Ended = true;
            detached = lane.Detached;
            _running--;
            Monitor.PulseAll(_sync);
        }

        if (detached)
        {
            try
            {
                // The worker left this checkpoint: the next pass collects it at once.
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
            runner.Next = null;
            if (_disposed)
            {
                return null;
            }

            _idle.Add(runner);
            while (runner.Next is null)
            {
                bool pulsed = !_disposed && Monitor.Wait(_sync, IdleTimeout);
                if (runner.Next is null && (!pulsed || _disposed))
                {
                    _idle.Remove(runner);
                    return null;
                }
            }

            return runner.Next;
        }
    }

    /// <summary>One checkpoint on a lane.</summary>
    private sealed class Lane
    {
        public Lane(Func<bool> checkpoint, ManualResetEventSlim signal)
        {
            Checkpoint = checkpoint;
            Signal = signal;
        }

        public Func<bool> Checkpoint { get; }

        public ManualResetEventSlim Signal { get; }

        // Guarded by the lanes' lock.
        public Outcome Outcome { get; set; }

        public bool Ended { get; set; }

        public bool Detached { get; set; }

        public long Pass { get; set; }
    }

    /// <summary>A lane thread's parking slot: the checkpoint the worker handed it, if any.</summary>
    private sealed class Runner
    {
        // Guarded by the lanes' lock.
        public Lane? Next { get; set; }
    }
}
