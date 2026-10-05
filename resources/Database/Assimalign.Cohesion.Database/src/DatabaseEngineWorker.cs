using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using Assimalign.Cohesion.Database.Internal;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The base of every engine-owned background worker: implements the blocking pump loop,
/// the loop's failure handling, and the per-database failure record, so an implementer only
/// supplies the trigger wait and the per-pass work.
/// </summary>
/// <remarks>
/// <para>
/// The public contract (<see cref="IDatabaseEngineWorker"/>) includes the engine's pump seam.
/// Members on this base — <see cref="Run"/>, <see cref="RunIteration"/>,
/// <see cref="WaitForTrigger"/> — exist for the <em>owning engine</em>, which spawns one dedicated
/// thread per worker at engine creation and cancels it on dispose. The <see cref="Run"/> loop
/// alternates <see cref="WaitForTrigger"/> and <see cref="RunIteration"/> until cancellation; passes
/// never overlap, even when a test calls <see cref="RunIteration"/> beside the engine's thread.
/// Timer-paced workers inherit the default trigger (wait out <see cref="Interval"/>);
/// signal-driven workers (a group-commit flusher woken by pending commits) override
/// <see cref="WaitForTrigger"/> with their own wake condition.
/// </para>
/// <para>
/// <b>One database's failure holds back that database only (#1268).</b> A pass visits the engine's
/// databases one by one. A failure that concerns one database is caught by the pass and reported
/// (<see cref="ReportFailure(string, Exception)"/>): the database gets a failure record, and the
/// passes that follow skip it (<see cref="BeginDatabase"/> returns false) until
/// <see cref="FailureBackoff"/> has passed, while every other database keeps the worker's full
/// pace. A repeated page write failure is therefore retried about once a second, and the
/// checkpoints of the engine's healthy databases never wait for it. The backoff bounds how often a
/// database is tried, not how long one try blocks: a pass may hand a database's work to a thread of
/// its own and leave it running, as the engines' checkpointers do with a checkpoint that hangs in
/// its device, reporting it unfinished until a later pass settles it. The record ends with the first
/// pass that finishes that database's work, and is forgotten when a pass no longer visits the
/// database (it was dropped or closed, or went offline). PostgreSQL's background writer,
/// checkpointer and WAL writer recover the same way: each catches an error per cycle, reports it,
/// releases what the cycle held and sleeps a second before its loop continues ("A write error is
/// likely to be repeated", <c>src/backend/postmaster/bgwriter.c:154-205</c>,
/// <c>checkpointer.c:286-346</c>, <c>walwriter.c:147-193</c>). Neo4j's checkpoint scheduler
/// counts consecutive failures and clears them on the next success
/// (<c>community/kernel/src/main/java/org/neo4j/wal/checkpoint/CheckPointScheduler.java:51-84</c>).
/// </para>
/// <para>
/// A failure that ends a whole pass — its work threw, or its trigger wait did — is the worker's
/// own: it is recorded, and <see cref="Run"/> sleeps <see cref="FailureBackoff"/> before the next
/// trigger wait, so a pass that fails at once cannot spin. The next pass that runs to its end
/// clears it.
/// </para>
/// <para>
/// <see cref="Fault"/> is set while the worker holds any failure, so the owning engine reports
/// <see cref="EngineState.Faulted"/> exactly while one of its workers has a database, or a pass,
/// whose failure it has not yet worked off. Every failure and every recovery is also written to
/// the <c>Assimalign.Cohesion.Database</c> event source (<c>docs/EVENT_SOURCES.md</c>).
/// </para>
/// <para>
/// A failure the worker cannot recover from is not the worker's to report: a storage that went
/// offline (a failed durable flush, #1243, a failed drain of its journal's append buffer, #1252, or
/// a failed file header write, #1268) takes its database offline, the
/// workers skip that database from then on, and the engine lists it in
/// <see cref="DatabaseEngine.OfflineDatabases"/>. Only an <see cref="OutOfMemoryException"/>
/// leaves the loop, and the thread with it, which ends the process: nothing ends the loop silently.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 3, #1259).</b> Every public member is non-virtual.
/// <see cref="Name"/>, <see cref="Kind"/> and <see cref="Interval"/> are fixed by the protected
/// constructor, so reading them makes no virtual call; a leaf supplies only the per-pass work
/// (<see cref="RunIterationCore"/>) and, when it is signal-driven, its trigger wait
/// (<see cref="WaitForTrigger"/>, the one lifecycle hook). The leaves live in the model
/// assemblies, so the constructor is <c>protected</c>.
/// </para>
/// </remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseEngineWorker : IDatabaseEngineWorker
{
    private readonly string _name;
    private readonly DatabaseEngineWorkerKind _kind;
    private readonly TimeSpan _interval;

    // Held for the whole of a pass: passes never overlap.
    private readonly object _passGate = new();

    // Guards everything below; never held across the pass's own work or an event write, so a
    // pass may report from any thread it starts.
    private readonly object _sync = new();
    private readonly Dictionary<string, DatabaseFailure> _databases = new(StringComparer.OrdinalIgnoreCase);
    private long _pass;
    private bool _passRunning;
    private bool _passReported;
    private long _order;

    // The failure of a whole pass (its work threw) or of a trigger wait, and how many passes in a
    // row ended that way; cleared by the next pass that runs to its end.
    private Exception? _passFault;
    private int _passFailures;

    private Exception? _fault;
    private int _consecutiveFailures;
    private long _failureCount;

    /// <summary>
    /// Initializes a new worker with its diagnostic name, its role and its cadence.
    /// </summary>
    /// <param name="name">
    /// The diagnostic name, unique within the owning engine (for example <c>sql-engine/checkpoint</c>).
    /// </param>
    /// <param name="kind">The worker's role.</param>
    /// <param name="interval">
    /// The cadence of the pump: the bound on how long the default trigger waits between passes. The
    /// owning engine validates the option it comes from; the value is fixed for the worker's life.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    protected DatabaseEngineWorker(string name, DatabaseEngineWorkerKind kind, TimeSpan interval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _kind = kind;
        _interval = interval;
    }

    /// <summary>
    /// Gets how long a database whose failure was reported is skipped before the worker tries it
    /// again, and how long <see cref="Run"/> sleeps after a pass that failed as a whole: one second,
    /// PostgreSQL's sleep after a background worker error
    /// (<c>src/backend/postmaster/checkpointer.c:340-345</c>), so a failure that repeats is retried
    /// at most about once a second instead of as fast as the trigger fires.
    /// </summary>
    public static TimeSpan FailureBackoff { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the diagnostic name of this worker, unique within its engine
    /// (for example <c>sql-engine/checkpoint</c>), as the constructor set it.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Gets the role of this worker, as the constructor set it.
    /// </summary>
    public DatabaseEngineWorkerKind Kind => _kind;

    /// <summary>
    /// Gets the cadence of the worker's pump: the bound on how long the default trigger waits
    /// between passes, as the constructor set it from the owning engine's options.
    /// </summary>
    public TimeSpan Interval => _interval;

    /// <summary>
    /// Gets the newest failure the worker still holds, or null while it is healthy: the failure of
    /// a pass that failed as a whole, or else the newest failure of a database the worker has not
    /// yet completed its work for.
    /// </summary>
    /// <remarks>
    /// One exception, never a list: a worker that fails on several databases holds the newest
    /// failure only. A database's failure stops being reported once a pass completes that
    /// database's work, whatever the worker's other databases do.
    /// </remarks>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>
    /// Gets the number of failed passes — passes that reported a failure, or failed as a whole —
    /// since the worker last held no failure; zero while it is healthy.
    /// </summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>
    /// Gets the number of passes that failed over the worker's life (diagnostics).
    /// </summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>
    /// Runs the worker's pump until <paramref name="cancellationToken"/> is signaled: waits for the
    /// worker's trigger (its <see cref="Interval"/> or an internal work signal), performs one pass,
    /// and repeats; after a pass that failed as a whole, or a trigger wait that failed, it sleeps
    /// <see cref="FailureBackoff"/> first. Blocking — the owning engine calls this on the dedicated
    /// thread it spawns for the worker.
    /// </summary>
    /// <param name="cancellationToken">Signaled to stop the pump.</param>
    /// <remarks>
    /// Returns only when <paramref name="cancellationToken"/> is signaled. A failure never ends the
    /// loop; only an <see cref="OutOfMemoryException"/> escapes it. A failure one database's work
    /// reported does not slow the loop: that database alone is skipped until its backoff passed.
    /// </remarks>
    public void Run(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                WaitForTrigger(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A trigger that cannot be waited for fails the worker as a whole: recorded, then
                // the backoff paces the next attempt so a wait that fails at once cannot spin.
                RecordWholeFailure(exception);
                BackOff(cancellationToken);
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            bool threw;
            try
            {
                RunPass(cancellationToken, out threw);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Only a pass that failed as a whole backs the loop off: a failure one database's
            // work reported holds back that database alone (BeginDatabase).
            if (threw)
            {
                BackOff(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Performs one bounded pump pass without waiting, and records its outcome: a pass that threw
    /// sets <see cref="Fault"/> until a later pass runs to its end; a database whose failure the
    /// pass reported keeps it until a later pass completes that database's work. Called by
    /// <see cref="Run"/>; the owning engine, or a test, may also call it directly, and a call made
    /// while another pass runs waits for that pass to end.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>True when the pass neither threw nor reported a failure; false when it did.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is signaled mid-pass.</exception>
    /// <remarks>
    /// The pass's own failures never propagate: they are recorded, and the caller reads the
    /// outcome from the return value and <see cref="Fault"/>. Only cancellation and an
    /// <see cref="OutOfMemoryException"/> are thrown. A pass that only skipped a database still
    /// backing off reported nothing new, so it returns true while <see cref="Fault"/> stays set.
    /// </remarks>
    public bool RunIteration(CancellationToken cancellationToken) => RunPass(cancellationToken, out _);

    /// <summary>
    /// Performs the worker's per-pass work: one bounded pass over the engine's open databases.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <remarks>
    /// <para>
    /// For each database it visits, the pass first asks <see cref="BeginDatabase"/>, and skips the
    /// database when that returns false. A failure that concerns one database is caught and
    /// reported (<see cref="ReportFailure(string, Exception)"/>), and the pass goes on to the next
    /// database, so one database's failure never stops another's work. Work left for later without
    /// a failure (a storage busy with a transaction, a checkpoint deferred to a running statement)
    /// is reported with <see cref="ReportUnfinished"/>, which keeps an earlier failure of that
    /// database recorded until the work is done. Anything the pass lets escape fails the pass as a
    /// whole.
    /// </para>
    /// <para>
    /// A database the pass does not begin is forgotten: its failure record ends with the pass. A
    /// pass therefore begins every open, online database, and skips an offline one without
    /// beginning it: the engine reports an offline database, not the worker.
    /// </para>
    /// </remarks>
    protected abstract void RunIterationCore(CancellationToken cancellationToken);

    /// <summary>
    /// Begins the current pass's work on <paramref name="database"/>, and reports whether the pass
    /// should do it: false while a failure reported for the database is still backing off
    /// (<see cref="FailureBackoff"/>, or the delay its report named). The pass then skips the
    /// database, whose failure stays recorded.
    /// </summary>
    /// <param name="database">The database's name, unique among the engine's open databases.</param>
    /// <returns>True when the pass should do the database's work; false to skip it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No pass is running.</exception>
    protected bool BeginDatabase(string database)
    {
        ArgumentNullException.ThrowIfNull(database);

        lock (_sync)
        {
            ThrowIfNoPassLocked();
            if (!_databases.TryGetValue(database, out var record))
            {
                return true;
            }

            record.VisitedPass = _pass;
            if (Stopwatch.GetTimestamp() < record.RetryAt)
            {
                record.UnfinishedPass = _pass;
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Records a failure of <paramref name="database"/> the current pass caught and moved past:
    /// the pass counts as failed, the worker's <see cref="Fault"/> is set at once, and later passes
    /// skip the database for <see cref="FailureBackoff"/>.
    /// </summary>
    /// <param name="database">The database's name.</param>
    /// <param name="exception">The failure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> or <paramref name="exception"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No pass is running.</exception>
    protected void ReportFailure(string database, Exception exception)
        => ReportFailure(database, exception, FailureBackoff);

    /// <summary>
    /// Records a failure of <paramref name="database"/> the current pass caught and moved past, and
    /// skips the database for <paramref name="retryAfter"/>: zero for work that paces its own
    /// retries (deferred undo, whose coordinator schedules each retry, #1226).
    /// </summary>
    /// <param name="database">The database's name.</param>
    /// <param name="exception">The failure.</param>
    /// <param name="retryAfter">How long later passes skip the database.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> or <paramref name="exception"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="retryAfter"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">No pass is running.</exception>
    protected void ReportFailure(string database, Exception exception, TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryAfter, TimeSpan.Zero);

        int failures;
        lock (_sync)
        {
            ThrowIfNoPassLocked();
            if (!_databases.TryGetValue(database, out var record))
            {
                record = new DatabaseFailure();
                _databases.Add(database, record);
            }

            record.Fault = exception;
            record.Failures++;
            record.Order = ++_order;
            record.VisitedPass = _pass;
            record.FailedPass = _pass;
            long now = Stopwatch.GetTimestamp();
            double delay = retryAfter.TotalSeconds * Stopwatch.Frequency;
            record.RetryAt = delay >= long.MaxValue - now ? long.MaxValue : now + (long)delay;
            failures = record.Failures;
            _passReported = true;
            Volatile.Write(ref _fault, exception);
        }

        DatabaseEventSource.Log.WorkerFailed(this, database, exception, failures);
    }

    /// <summary>
    /// Reports that the current pass left work of <paramref name="database"/> for a later pass
    /// without a failure: a storage busy with a transaction, a checkpoint a running statement took
    /// over. A failure reported for the database earlier stays recorded until a pass finishes its
    /// work; a database with no failure record is unaffected.
    /// </summary>
    /// <param name="database">The database's name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No pass is running.</exception>
    protected void ReportUnfinished(string database)
    {
        ArgumentNullException.ThrowIfNull(database);

        lock (_sync)
        {
            ThrowIfNoPassLocked();
            if (_databases.TryGetValue(database, out var record))
            {
                record.VisitedPass = _pass;
                record.UnfinishedPass = _pass;
            }
        }
    }

    /// <summary>
    /// Blocks until the worker's next pump pass should run. The default waits out
    /// <see cref="Interval"/> (returning early on cancellation); signal-driven
    /// workers override this with their own wake condition.
    /// </summary>
    /// <param name="cancellationToken">Signaled to stop the pump; the wait must return promptly.</param>
    protected virtual void WaitForTrigger(CancellationToken cancellationToken)
        => cancellationToken.WaitHandle.WaitOne(Interval);

    private bool RunPass(CancellationToken cancellationToken, out bool threw)
    {
        lock (_passGate)
        {
            lock (_sync)
            {
                _pass++;
                _passRunning = true;
                _passReported = false;
            }

            Exception? thrown = null;
            try
            {
                RunIterationCore(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AbandonPass();
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                thrown = exception;
            }
            catch
            {
                AbandonPass();
                throw;
            }

            threw = thrown is not null;
            return SettlePass(thrown, cancellationToken.IsCancellationRequested);
        }
    }

    /// <summary>
    /// Ends a pass that was cancelled, or escaped: nothing it did is settled.
    /// </summary>
    private void AbandonPass()
    {
        lock (_sync)
        {
            _passRunning = false;
        }
    }

    /// <summary>
    /// Settles a pass that ended: records a whole-pass failure, or ends the whole-pass failure and
    /// every database failure whose work the pass finished, then publishes the worker's state.
    /// </summary>
    /// <returns>True when the pass neither threw nor reported a failure.</returns>
    private bool SettlePass(Exception? thrown, bool cancelled)
    {
        bool failed;
        int passFailures = 0;
        int recoveredPassFailures = 0;
        List<(string Database, int Failures)>? recovered = null;

        lock (_sync)
        {
            _passRunning = false;
            failed = _passReported || thrown is not null;

            if (thrown is not null)
            {
                // The pass did not reach every database, so their records stay as they are.
                _passFault = thrown;
                passFailures = ++_passFailures;
            }
            else
            {
                if (_passFault is not null)
                {
                    recoveredPassFailures = _passFailures;
                    _passFault = null;
                    _passFailures = 0;
                }

                // A cancelled pass stopped early: what it did not visit is not done.
                if (!cancelled && _databases.Count > 0)
                {
                    List<string>? ended = null;
                    foreach (var (database, record) in _databases)
                    {
                        if (record.VisitedPass != _pass)
                        {
                            // Not visited: dropped, closed or offline. Nothing is left to recover.
                            (ended ??= []).Add(database);
                        }
                        else if (record.FailedPass != _pass && record.UnfinishedPass != _pass)
                        {
                            // The pass finished the work the failure had left: recovered.
                            (ended ??= []).Add(database);
                            (recovered ??= []).Add((database, record.Failures));
                        }
                    }

                    if (ended is not null)
                    {
                        foreach (string database in ended)
                        {
                            _databases.Remove(database);
                        }
                    }
                }
            }

            if (failed)
            {
                _failureCount++;
                _consecutiveFailures++;
            }

            PublishFaultLocked();
        }

        if (thrown is not null)
        {
            DatabaseEventSource.Log.WorkerFailed(this, string.Empty, thrown, passFailures);
        }

        if (recoveredPassFailures > 0)
        {
            DatabaseEventSource.Log.WorkerRecovered(this, string.Empty, recoveredPassFailures);
        }

        if (recovered is not null)
        {
            foreach (var (database, failures) in recovered)
            {
                DatabaseEventSource.Log.WorkerRecovered(this, database, failures);
            }
        }

        return !failed;
    }

    /// <summary>
    /// Records a trigger wait that failed: a failure of the worker as a whole, which the next pass
    /// that runs to its end clears.
    /// </summary>
    private void RecordWholeFailure(Exception exception)
    {
        int passFailures;
        lock (_sync)
        {
            _passFault = exception;
            passFailures = ++_passFailures;
            _failureCount++;
            _consecutiveFailures++;
            PublishFaultLocked();
        }

        DatabaseEventSource.Log.WorkerFailed(this, string.Empty, exception, passFailures);
    }

    /// <summary>
    /// Publishes <see cref="Fault"/> from the failures the worker holds, and resets
    /// <see cref="ConsecutiveFailures"/> once it holds none.
    /// </summary>
    private void PublishFaultLocked()
    {
        Exception? fault = _passFault;
        if (fault is null)
        {
            long newest = 0;
            foreach (var record in _databases.Values)
            {
                if (record.Order > newest)
                {
                    newest = record.Order;
                    fault = record.Fault;
                }
            }
        }

        if (fault is null)
        {
            _consecutiveFailures = 0;
        }

        Volatile.Write(ref _fault, fault);
    }

    private void ThrowIfNoPassLocked()
    {
        if (!_passRunning)
        {
            throw new InvalidOperationException(
                "A worker reports on a database only from within a pass (RunIterationCore).");
        }
    }

    private static void BackOff(CancellationToken cancellationToken)
        => cancellationToken.WaitHandle.WaitOne(FailureBackoff);

    /// <summary>
    /// The failure record of one database: the failure, how often it repeated, when the database
    /// may be tried again, and what the passes since did with it.
    /// </summary>
    private sealed class DatabaseFailure
    {
        public Exception Fault = null!;
        public int Failures;
        public long Order;
        public long RetryAt;
        public long VisitedPass;
        public long FailedPass;
        public long UnfinishedPass;
    }
}
