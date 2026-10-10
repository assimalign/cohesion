using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

/// <summary>
/// The lock-wait stress probe of #1390: the workload of the #1370 review's stress runs, which saw
/// two to five lock waits per run last more than 10 s. Cascading parent deletes, child and
/// grandchild writers that insert, re-parent, update and delete, in explicit snapshot and
/// read-committed transactions and in auto-commit, while a thread runs the version purge. The
/// probe watches every lock wait through the transaction kernel's event source and records each
/// one that lasts a second or more, with what the kernel reported blocking it while it waited
/// (<c>LockWaitBlocked</c>) and what the workers named there were doing.
/// </summary>
/// <remarks>
/// <para>
/// The run continues past its duration until the lock manager has broken enough deadlocks and the
/// workers have committed enough, at most <see cref="Ceiling"/> longer: the deadlocks are the
/// cycles between cascades, which lock parent rows before child rows, and child writers, which
/// lock child rows before parent rows, and a cycle the detector missed was the cause of every long
/// wait on the base. The run then stops starting statements and lets the running ones end, so a
/// wait that would never end ages past the long-wait bound instead of being canceled with the
/// run. A wait <see cref="CancelAfter"/> old is canceled, so a stuck wait fails the probe rather
/// than hanging it.
/// </para>
/// <para>
/// On the base (<c>19b1a495</c>), three 60-second Release runs saw 40, 40 and 56 waits pass 10 s:
/// the members of two- and three-transaction cycles whose wait-for edges the lock manager had lost,
/// and the waits queued behind them (#1390). With the fix, five runs saw no wait reach 1 s.
/// </para>
/// </remarks>
internal sealed class SqlLockWaitProbe
{
    /// <summary>The parent rows the cascades delete and the children reference.</summary>
    internal const int Parents = 6;

    /// <summary>The child keys.</summary>
    internal const int Children = 30;

    /// <summary>The grandchild keys.</summary>
    internal const int Grandchildren = 30;

    /// <summary>The longest a run continues past its duration for the work it needs.</summary>
    internal static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    /// <summary>The deadlocks the lock manager must break before a run may stop.</summary>
    internal const int MinimumDeadlocks = 25;

    /// <summary>The commits the workers must make before a run may stop.</summary>
    internal const int MinimumCommits = 200;

    /// <summary>A wait at least this long is recorded.</summary>
    internal static readonly TimeSpan SlowWait = TimeSpan.FromSeconds(1);

    /// <summary>A wait at least this long fails the probe: the bound of #1390.</summary>
    internal static readonly TimeSpan LongWait = TimeSpan.FromSeconds(10);

    /// <summary>A wait this old is canceled, so a stuck wait cannot hang the run.</summary>
    internal static readonly TimeSpan CancelAfter = TimeSpan.FromSeconds(12);

    private const string TransactionsSource = "Assimalign.Cohesion.Database.Transactions";

    private static readonly AsyncLocal<int> CurrentWorker = new();

    private readonly SqlDatabase _database;
    private readonly string _name;
    private readonly Worker[] _workers;
    private readonly ConcurrentDictionary<long, int> _sequenceWorkers = new();
    private readonly ConcurrentDictionary<long, Wait> _waiting = new();
    private readonly ConcurrentQueue<SqlLockWait> _slow = new();
    private readonly ConcurrentDictionary<string, int> _unexpected = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _statements;
    private long _commits;
    private long _deadlocks;
    private long _conflicts;
    private long _violations;
    private long _waits;

    private SqlLockWaitProbe(SqlDatabase database, string name, int workers)
    {
        _database = database;
        _name = name;
        _workers = Enumerable.Range(0, workers).Select(id => new Worker(id, this)).ToArray();
    }

    /// <summary>Runs the probe and returns what it saw.</summary>
    /// <param name="duration">The shortest time the workers run before the run may stop.</param>
    /// <param name="deleters">The workers that delete and re-insert parents, which cascade.</param>
    /// <param name="childWriters">The workers that write children and grandchildren.</param>
    /// <returns>The result.</returns>
    internal static async Task<SqlLockWaitProbeResult> RunAsync(TimeSpan duration, int deleters = 3, int childWriters = 5)
    {
        string name = "lock-wait-probe-" + Guid.NewGuid().ToString("N");
        await using var engine = SqlDatabaseEngine.Create("lock-wait-probe", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync(name);
        await using (var setup = await database.CreateSessionAsync())
        {
            await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY, v INT)");
            await setup.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, v INT, CONSTRAINT fk_c FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
            await setup.ExecuteAsync("CREATE TABLE g (id INT PRIMARY KEY, cid INT, CONSTRAINT fk_g FOREIGN KEY(cid) REFERENCES c(id) ON DELETE CASCADE)");
            await setup.ExecuteAsync("INSERT INTO p (id, v) VALUES " + string.Join(", ", Enumerable.Range(1, Parents).Select(id => $"({id}, 0)")));
            await setup.ExecuteAsync("INSERT INTO c (id, pid, v) VALUES " + string.Join(", ", Enumerable.Range(1, Children).Select(id => $"({id}, {id % Parents + 1}, 0)")));
            await setup.ExecuteAsync("INSERT INTO g (id, cid) VALUES " + string.Join(", ", Enumerable.Range(1, Grandchildren).Select(id => $"({id}, {id})")));
        }

        var probe = new SqlLockWaitProbe(database, name, deleters + childWriters);
        using var listener = new Listener(probe);
        using var stop = new CancellationTokenSource();
        var workers = probe._workers.Select(worker => Task.Run(() => probe.RunWorkerAsync(worker, deleter: worker.Id < deleters, stop.Token))).ToArray();

        // The purge and the watchdog have threads of their own: on a small runner the workers hold
        // the pool's few threads, and a pooled loop could wait out the whole run.
        using var finished = new CancellationTokenSource();
        var purge = new Thread(() => probe.Purge(stop.Token)) { IsBackground = true, Name = "lock-wait-probe-purge" };
        var watchdog = new Thread(() => probe.Watch(finished.Token)) { IsBackground = true, Name = "lock-wait-probe-watchdog" };
        purge.Start();
        watchdog.Start();

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < duration || (!probe.HasExercisedTheDetector && elapsed.Elapsed < duration + Ceiling))
        {
            await Task.Delay(50);
        }

        // Stop starting statements and let the running ones end: a wait that never ends ages past
        // the long-wait bound, and the watchdog cancels it.
        var ran = elapsed.Elapsed;
        stop.Cancel();
        await Task.WhenAll(workers);
        purge.Join();
        finished.Cancel();
        watchdog.Join();

        return new SqlLockWaitProbeResult(
            ran,
            Interlocked.Read(ref probe._statements),
            Interlocked.Read(ref probe._commits),
            Interlocked.Read(ref probe._deadlocks),
            Interlocked.Read(ref probe._conflicts),
            Interlocked.Read(ref probe._violations),
            Interlocked.Read(ref probe._waits),
            probe._slow.OrderBy(wait => wait.Started).ToArray(),
            probe._unexpected.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    private bool HasExercisedTheDetector
        => Interlocked.Read(ref _deadlocks) >= MinimumDeadlocks && Interlocked.Read(ref _commits) >= MinimumCommits;

    private async Task RunWorkerAsync(Worker worker, bool deleter, CancellationToken stop)
    {
        CurrentWorker.Value = worker.Id + 1;
        var random = new Random(worker.Id * 7919 + 13);
        await using var session = await _database.CreateSessionAsync();

        while (!stop.IsCancellationRequested)
        {
            // An explicit snapshot or read-committed transaction, or auto-commit.
            int mode = random.Next(3);
            SqlDatabaseTransaction? transaction = null;
            try
            {
                if (mode != 0)
                {
                    worker.Enter("begin");
                    transaction = await session.BeginTransactionAsync(mode == 1 ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted);
                    Volatile.Write(ref worker.Sequence, (long)transaction.Context.Sequence.Value);
                }

                bool usable = true;
                foreach (string sql in deleter ? DeleterStatements(random) : ChildStatements(random))
                {
                    if (stop.IsCancellationRequested)
                    {
                        break;
                    }

                    worker.Enter(sql);
                    usable = await ExecuteAsync(session, worker, sql, explicitTransaction: transaction is not null);
                    if (!usable)
                    {
                        break;
                    }

                    if (transaction is not null && random.Next(3) == 0)
                    {
                        worker.Enter("between statements");
                        await Task.Delay(random.Next(0, 3));
                    }
                }

                if (transaction is not null && usable)
                {
                    worker.Enter("commit");
                    await transaction.CommitAsync();
                    Interlocked.Increment(ref _commits);
                }
            }
            catch (DatabaseTransactionAbortedException)
            {
                // A commit after a conflict the transaction could not survive.
                Interlocked.Increment(ref _conflicts);
            }
            catch (Exception exception)
            {
                Unexpected(exception);
            }
            finally
            {
                if (transaction is not null)
                {
                    // Rolls back whatever did not commit.
                    worker.Enter("rollback");
                    try
                    {
                        await transaction.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        Unexpected(exception);
                    }
                }

                Volatile.Write(ref worker.Sequence, 0);
                worker.Enter("idle");
            }
        }
    }

    /// <summary>
    /// Runs one statement. A conflict the engine resolves (a deadlock victim, first-updater-wins, a
    /// constraint the random keys break) is counted; a deadlock or a write conflict ends an explicit
    /// transaction, which then rolls back. A canceled wait is the watchdog's, already recorded.
    /// </summary>
    /// <returns>Whether the transaction may run its next statement.</returns>
    private async Task<bool> ExecuteAsync(SqlDatabaseSession session, Worker worker, string sql, bool explicitTransaction)
    {
        try
        {
            await session.ExecuteAsync(sql, cancellationToken: worker.NextStatement());
            Interlocked.Increment(ref _statements);
            if (!explicitTransaction)
            {
                Interlocked.Increment(ref _commits);
            }

            return true;
        }
        catch (SqlConstraintViolationException)
        {
            Interlocked.Increment(ref _violations);
            return true;
        }
        catch (DatabaseTransactionDeadlockException)
        {
            Interlocked.Increment(ref _deadlocks);
            return false;
        }
        catch (DatabaseTransactionAbortedException)
        {
            Interlocked.Increment(ref _conflicts);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static List<string> DeleterStatements(Random random)
    {
        int parent = random.Next(1, Parents + 1);
        var statements = new List<string> { $"DELETE FROM p WHERE id = {parent}", $"INSERT INTO p (id, v) VALUES ({parent}, 0)" };
        int children = random.Next(0, 4);
        for (int i = 0; i < children; i++)
        {
            int child = random.Next(1, Children + 1);
            statements.Add($"INSERT INTO c (id, pid, v) VALUES ({child}, {parent}, 0)");
            if (random.Next(2) == 0)
            {
                statements.Add($"INSERT INTO g (id, cid) VALUES ({random.Next(1, Grandchildren + 1)}, {child})");
            }
        }

        return statements;
    }

    private static List<string> ChildStatements(Random random)
    {
        var statements = new List<string>();
        int count = random.Next(1, 4);
        for (int i = 0; i < count; i++)
        {
            int child = random.Next(1, Children + 1);
            int parent = random.Next(1, Parents + 1);
            int grandchild = random.Next(1, Grandchildren + 1);
            statements.Add(random.Next(7) switch
            {
                0 => $"INSERT INTO c (id, pid, v) VALUES ({child}, {parent}, 0)",
                1 => $"UPDATE c SET v = v + 1 WHERE id = {child}",
                2 => $"UPDATE c SET pid = {parent} WHERE id = {child}",
                3 => $"DELETE FROM c WHERE id = {child}",
                4 => $"INSERT INTO g (id, cid) VALUES ({grandchild}, {child})",
                5 => $"UPDATE g SET cid = {child} WHERE id = {grandchild}",
                _ => $"DELETE FROM g WHERE id = {grandchild}",
            });
        }

        return statements;
    }

    private void Purge(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                _database.Coordinator.RunVersionPurgePass(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Unexpected(exception);
            }

            Thread.Sleep(5);
        }
    }

    // Cancels each wait that reached CancelAfter, so a stuck wait ends and is recorded.
    private void Watch(CancellationToken finished)
    {
        while (!finished.IsCancellationRequested)
        {
            Thread.Sleep(100);
            foreach (var wait in _waiting.Values)
            {
                if (wait.Age >= CancelAfter && Interlocked.Exchange(ref wait.Canceled, 1) == 0)
                {
                    wait.WorkerAtCancel = DescribeWorker(wait.Sequence);
                    wait.BlockerWorkersAtCancel = DescribeBlockerWorkers(wait.Blockers);
                    if (_sequenceWorkers.TryGetValue(wait.Sequence, out int id))
                    {
                        _workers[id].CancelStatement();
                    }
                }
            }
        }
    }

    private void Unexpected(Exception exception)
        => _unexpected.AddOrUpdate(exception.GetType().Name + ": " + exception.Message, 1, (_, count) => count + 1);

    private string DescribeWorker(long sequence)
        => _sequenceWorkers.TryGetValue(sequence, out int id) ? _workers[id].Describe() : "no worker";

    // The workers of the transactions a LockWaitBlocked report names ("<sequence>:<mode>").
    private string? DescribeBlockerWorkers(string? blockers)
        => blockers is null
            ? null
            : string.Join("; ", Regex.Matches(blockers, @"(\d+):").Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Distinct().Select(sequence => $"{sequence}: {DescribeWorker(sequence)}"));

    private void OnWaitStarted(long sequence, string resource, string mode)
    {
        Interlocked.Increment(ref _waits);
        _waiting[sequence] = new Wait(sequence, resource, mode, _clock);
    }

    private void OnWaitBlocked(long sequence, string blockers)
    {
        if (_waiting.TryGetValue(sequence, out var wait))
        {
            wait.Blockers = blockers;
            wait.WorkerWhenBlocked = DescribeWorker(sequence);
            wait.BlockerWorkersWhenBlocked = DescribeBlockerWorkers(blockers);
        }
    }

    private void OnWaitStopped(long sequence, string outcome, double milliseconds)
    {
        if (_waiting.TryRemove(sequence, out var wait) && milliseconds >= SlowWait.TotalMilliseconds)
        {
            _slow.Enqueue(new SqlLockWait(
                wait.Started, sequence, wait.Resource, wait.Mode, milliseconds, outcome,
                wait.WorkerAtCancel ?? wait.WorkerWhenBlocked ?? DescribeWorker(sequence),
                wait.Blockers,
                wait.BlockerWorkersAtCancel ?? wait.BlockerWorkersWhenBlocked));
        }
    }

    /// <summary>One lock wait in progress.</summary>
    private sealed class Wait
    {
        private readonly Stopwatch _clock;

        public Wait(long sequence, string resource, string mode, Stopwatch clock)
        {
            Sequence = sequence;
            Resource = resource;
            Mode = mode;
            _clock = clock;
            Started = clock.Elapsed;
        }

        public long Sequence { get; }

        public string Resource { get; }

        public string Mode { get; }

        public TimeSpan Started { get; }

        public TimeSpan Age => _clock.Elapsed - Started;

        public volatile string? Blockers;

        public volatile string? WorkerWhenBlocked;

        public volatile string? BlockerWorkersWhenBlocked;

        public volatile string? WorkerAtCancel;

        public volatile string? BlockerWorkersAtCancel;

        public int Canceled;
    }

    /// <summary>One session of the workload, and what it is doing.</summary>
    private sealed class Worker
    {
        private readonly SqlLockWaitProbe _probe;
        private CancellationTokenSource _statement = new();
        private volatile string _phase = "idle";
        private long _sinceTicks;

        public Worker(int id, SqlLockWaitProbe probe)
        {
            Id = id;
            _probe = probe;
        }

        public int Id { get; }

        /// <summary>The sequence of the worker's explicit transaction, zero outside one.</summary>
        public long Sequence;

        public void Enter(string phase)
        {
            Volatile.Write(ref _sinceTicks, _probe._clock.Elapsed.Ticks);
            _phase = phase;
        }

        public string Describe()
        {
            double seconds = (_probe._clock.Elapsed - TimeSpan.FromTicks(Volatile.Read(ref _sinceTicks))).TotalSeconds;
            long sequence = Volatile.Read(ref Sequence);
            return string.Create(CultureInfo.InvariantCulture,
                $"worker {Id} in '{_phase}' for {seconds:F2} s, {(sequence == 0 ? "auto-commit" : $"transaction {sequence}")}");
        }

        public CancellationToken NextStatement()
        {
            var next = new CancellationTokenSource();
            Interlocked.Exchange(ref _statement, next).Dispose();
            return next.Token;
        }

        public void CancelStatement()
        {
            try
            {
                Volatile.Read(ref _statement).Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The statement ended and the worker moved on.
            }
        }
    }

    /// <summary>
    /// Observes the transaction kernel's events for the probe's database: the transactions each
    /// worker begins (so a waiting or blocking sequence names its worker), every lock wait's start
    /// and stop, and the report of a wait still queued at the slow-lock-wait threshold.
    /// </summary>
    private sealed class Listener : EventListener
    {
        // Assigned after the base constructor has enabled the sources that already exist; events
        // that arrive before it are not the probe's database's.
        private readonly SqlLockWaitProbe? _probe;

        public Listener(SqlLockWaitProbe probe)
        {
            _probe = probe;
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == TransactionsSource)
            {
                // Transactions | Locks, and the warnings; the threshold is the probe's slow wait.
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x3, new Dictionary<string, string?>
                {
                    ["SlowLockWaitThresholdMs"] = SlowWait.TotalMilliseconds.ToString(CultureInfo.InvariantCulture),
                });
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs written)
        {
            if (_probe is not { } probe || written.Payload is not { Count: > 1 } payload || !Equals(payload[0], probe._name))
            {
                return;
            }

            long sequence = (long)payload[1]!;
            switch (written.EventName)
            {
                case "TransactionBegun" when CurrentWorker.Value is int worker and > 0:
                    probe._sequenceWorkers[sequence] = worker - 1;
                    break;
                case "LockWaitStart":
                    probe.OnWaitStarted(sequence, (string)payload[2]!, (string)payload[3]!);
                    break;
                case "LockWaitBlocked":
                    probe.OnWaitBlocked(sequence, (string)payload[5]!);
                    break;
                case "LockWaitStop":
                    probe.OnWaitStopped(sequence, (string)payload[2]!, (double)payload[3]!);
                    break;
            }
        }
    }
}

/// <summary>A lock wait the probe recorded: one that lasted at least <see cref="SqlLockWaitProbe.SlowWait"/>.</summary>
/// <param name="Started">When the wait began, from the start of the run.</param>
/// <param name="Sequence">The waiting transaction.</param>
/// <param name="Resource">The resource it waited for, by kind and object.</param>
/// <param name="Mode">The mode it requested.</param>
/// <param name="Milliseconds">How long it waited.</param>
/// <param name="Outcome">How the wait ended.</param>
/// <param name="Worker">What the waiting worker was doing.</param>
/// <param name="Blockers">What the kernel reported blocking the wait while it waited, if it did.</param>
/// <param name="BlockerWorkers">What the workers of the blocking transactions were doing.</param>
internal sealed record SqlLockWait(TimeSpan Started, long Sequence, string Resource, string Mode, double Milliseconds, string Outcome,
    string Worker, string? Blockers, string? BlockerWorkers)
{
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"at {Started.TotalSeconds:F2} s transaction {Sequence} waited {Milliseconds:F0} ms for {Mode} on {Resource} ({Outcome}); {Worker}") +
            (Blockers is null ? "" : $"{Environment.NewLine}    blocked by {Blockers}{Environment.NewLine}    blockers: {BlockerWorkers}");
}

/// <summary>What one probe run saw.</summary>
/// <param name="Ran">How long the workers ran before the run stopped starting statements.</param>
/// <param name="Statements">The statements that completed.</param>
/// <param name="Commits">The transactions that committed, auto-commit statements included.</param>
/// <param name="Deadlocks">The statements chosen as deadlock victims.</param>
/// <param name="Conflicts">The statements and commits that failed a write conflict.</param>
/// <param name="Violations">The statements a constraint refused.</param>
/// <param name="Waits">The lock requests that waited.</param>
/// <param name="SlowWaits">The waits that lasted at least <see cref="SqlLockWaitProbe.SlowWait"/>.</param>
/// <param name="Unexpected">Every other failure, by type and message.</param>
internal sealed record SqlLockWaitProbeResult(
    TimeSpan Ran,
    long Statements,
    long Commits,
    long Deadlocks,
    long Conflicts,
    long Violations,
    long Waits,
    IReadOnlyList<SqlLockWait> SlowWaits,
    IReadOnlyDictionary<string, int> Unexpected)
{
    /// <summary>The recorded waits that lasted at least <paramref name="bound"/>.</summary>
    public IReadOnlyList<SqlLockWait> WaitsOf(TimeSpan bound) => SlowWaits.Where(wait => wait.Milliseconds >= bound.TotalMilliseconds).ToArray();

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"ran {Ran.TotalSeconds:F1} s: {Statements} statements, {Commits} commits, {Deadlocks} deadlock victims, ")
            .Append(CultureInfo.InvariantCulture, $"{Conflicts} conflicts, {Violations} constraint violations, {Waits} lock waits; ")
            .Append(CultureInfo.InvariantCulture, $"waits of {SqlLockWaitProbe.SlowWait.TotalSeconds} s or more: {SlowWaits.Count}, ")
            .Append(CultureInfo.InvariantCulture, $"of {SqlLockWaitProbe.LongWait.TotalSeconds} s or more: {WaitsOf(SqlLockWaitProbe.LongWait).Count}");
        foreach (var wait in SlowWaits)
        {
            text.AppendLine().Append("  ").Append(wait);
        }

        foreach (var (error, count) in Unexpected)
        {
            text.AppendLine().Append(CultureInfo.InvariantCulture, $"  unexpected x{count}: {error}");
        }

        return text.ToString();
    }
}
