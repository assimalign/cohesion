using System;
using System.Threading;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// The guided base class for engine-owned background workers: implements the blocking pump loop,
/// and the loop's failure handling, so an implementer only supplies the trigger wait and the
/// per-pass work.
/// </summary>
/// <remarks>
/// <para>
/// The public contract (<see cref="IDatabaseEngineWorker"/>) includes the engine's pump seam.
/// Members on this base — <see cref="Run"/>, <see cref="RunIteration"/>,
/// <see cref="WaitForTrigger"/> — exist for the <em>owning engine</em>, which spawns one dedicated
/// thread per worker at engine creation and cancels it on dispose. Nothing outside the engine may
/// pump a worker; a worker never runs twice concurrently because exactly one engine-internal
/// scheduler drives it. The <see cref="Run"/> loop alternates <see cref="WaitForTrigger"/> and
/// <see cref="RunIteration"/> until cancellation. Timer-paced workers inherit the default trigger
/// (wait out <see cref="Interval"/>); signal-driven workers (a group-commit flusher woken by
/// pending commits) override <see cref="WaitForTrigger"/> with their own wake condition.
/// </para>
/// <para>
/// <b>A failed pass never ends the loop (#1268).</b> A pass fails when its work throws, or when it
/// reports a failure it moved past (<see cref="ReportFailure"/>: one database's checkpoint failed,
/// and the pass went on to the next database). The failure is recorded (<see cref="Fault"/>,
/// <see cref="ConsecutiveFailures"/>, <see cref="FailureCount"/>), the loop sleeps
/// <see cref="FailureBackoff"/>, and the next pass runs as usual. A pass that completes its work
/// without a failure clears the record, so the owning engine reports
/// <see cref="EngineState.Faulted"/> exactly while one of its workers keeps failing. PostgreSQL's
/// background writer, checkpointer and WAL writer recover the same way: each catches an error
/// per cycle, reports it, releases what the cycle held and sleeps a second before its loop
/// continues ("A write error is likely to be repeated", <c>src/backend/postmaster/bgwriter.c:154-205</c>,
/// <c>checkpointer.c:286-346</c>, <c>walwriter.c:147-193</c>). Neo4j's checkpoint scheduler
/// counts consecutive failures and clears them on the next success
/// (<c>community/kernel/src/main/java/org/neo4j/wal/checkpoint/CheckPointScheduler.java:51-84</c>).
/// </para>
/// <para>
/// A failure the worker cannot recover from is not the worker's to report: a storage that went
/// offline (a failed durable flush or file header write, #1243) takes its database offline, the
/// workers skip that database from then on, and the engine lists it in
/// <see cref="IDatabaseEngine.OfflineDatabases"/>. Only an <see cref="OutOfMemoryException"/>
/// leaves the loop, and the thread with it, which ends the process: nothing ends the loop silently.
/// </para>
/// </remarks>
public abstract class DatabaseEngineWorker : IDatabaseEngineWorker
{
    private Exception? _fault;
    private int _consecutiveFailures;
    private long _failureCount;

    // Incremented by every ReportFailure; a pass compares it before and after its work, so a
    // failure reported from inside the work marks the pass failed without per-pass state. The
    // failure itself is kept beside it and never cleared, so a pass that failed records its
    // failure even when another pass ran clean in between (the engine runs one pass at a time;
    // a test may call RunIteration beside the worker's thread).
    private long _reportedFailures;
    private Exception? _lastReported;

    /// <summary>
    /// Initializes a new worker.
    /// </summary>
    protected DatabaseEngineWorker() { }

    /// <summary>
    /// Gets how long <see cref="Run"/> sleeps after a failed pass before it waits for the next
    /// trigger: one second, PostgreSQL's sleep after a background worker error
    /// (<c>src/backend/postmaster/checkpointer.c:340-345</c>), so a failure that repeats is retried
    /// at most about once a second instead of as fast as the trigger fires.
    /// </summary>
    public static TimeSpan FailureBackoff { get; } = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract DatabaseEngineWorkerKind Kind { get; }

    /// <inheritdoc />
    public abstract TimeSpan Interval { get; }

    /// <summary>
    /// Gets the most recent failure of the worker, or null while it is healthy. Set by a pass that
    /// failed and kept until a later pass completes its work without a failure.
    /// </summary>
    /// <remarks>
    /// One exception, never a list: a worker that fails on every pass holds the last failure only.
    /// </remarks>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>
    /// Gets the number of passes that failed since the worker last completed a pass's work without
    /// a failure; zero while it is healthy.
    /// </summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>
    /// Gets the number of passes that failed over the worker's life (diagnostics).
    /// </summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>
    /// Runs the worker's pump until <paramref name="cancellationToken"/> is signaled: waits for the
    /// worker's trigger (its <see cref="Interval"/> or an internal work signal), performs one pass,
    /// and repeats; after a failed pass, or a trigger wait that failed, it sleeps
    /// <see cref="FailureBackoff"/> first. Blocking — the owning engine calls this on the dedicated
    /// thread it spawns for the worker.
    /// </summary>
    /// <param name="cancellationToken">Signaled to stop the pump.</param>
    /// <remarks>
    /// Returns only when <paramref name="cancellationToken"/> is signaled. A failure never ends the
    /// loop; only an <see cref="OutOfMemoryException"/> escapes it.
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
                // A trigger that cannot be waited for is a failed pass: recorded, then the
                // backoff paces the next attempt so a wait that fails at once cannot spin.
                ReportFailure(exception);
                RecordFailedPass(exception);
                BackOff(cancellationToken);
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            bool succeeded;
            try
            {
                succeeded = RunIteration(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!succeeded)
            {
                BackOff(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Performs one bounded pump pass without waiting, and records its outcome: a pass that threw
    /// or reported a failure (<see cref="ReportFailure"/>) sets <see cref="Fault"/>; a pass that
    /// completed its work without one clears it. Called by <see cref="Run"/>; the owning engine,
    /// or a test, may also call it directly.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>True when the pass reported no failure; false when it failed.</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is signaled mid-pass.</exception>
    /// <remarks>
    /// The pass's own failures never propagate: they are recorded, and the caller reads the
    /// outcome from the return value and <see cref="Fault"/>. Only cancellation and an
    /// <see cref="OutOfMemoryException"/> are thrown.
    /// </remarks>
    public bool RunIteration(CancellationToken cancellationToken)
    {
        long reportedBefore = Interlocked.Read(ref _reportedFailures);
        bool completed;
        Exception? thrown = null;

        try
        {
            completed = RunIterationCore(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            thrown = exception;
            completed = false;
            ReportFailure(exception);
        }

        if (thrown is not null || Interlocked.Read(ref _reportedFailures) != reportedBefore)
        {
            RecordFailedPass(thrown ?? Volatile.Read(ref _lastReported));
            return false;
        }

        if (completed)
        {
            // The work the earlier failures left behind is done: the worker is healthy again.
            Volatile.Write(ref _consecutiveFailures, 0);
            Volatile.Write(ref _fault, null);
        }

        return true;
    }

    /// <summary>
    /// Performs the worker's per-pass work: one bounded pass over the engine's open databases.
    /// </summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>
    /// True when the pass did all the work it found; false when it left some for a later pass
    /// without a failure (a storage busy with a transaction, an undo whose retry is not due yet),
    /// which keeps an earlier failure recorded until that work completes.
    /// </returns>
    /// <remarks>
    /// A failure that concerns one database is caught and reported (<see cref="ReportFailure"/>),
    /// and the pass goes on to the next database, so one database's failure never stops another's
    /// work. Anything else the core throws fails the pass the same way.
    /// </remarks>
    protected abstract bool RunIterationCore(CancellationToken cancellationToken);

    /// <summary>
    /// Records a failure the current pass caught and moved past: the pass counts as failed, the
    /// worker's <see cref="Fault"/> is set at once, and <see cref="Run"/> backs off after the pass.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    protected void ReportFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Volatile.Write(ref _lastReported, exception);
        Volatile.Write(ref _fault, exception);
        Interlocked.Increment(ref _reportedFailures);
    }

    /// <summary>
    /// Blocks until the worker's next pump pass should run. The default waits out
    /// <see cref="Interval"/> (returning early on cancellation); signal-driven
    /// workers override this with their own wake condition.
    /// </summary>
    /// <param name="cancellationToken">Signaled to stop the pump; the wait must return promptly.</param>
    protected virtual void WaitForTrigger(CancellationToken cancellationToken)
        => cancellationToken.WaitHandle.WaitOne(Interval);

    private void RecordFailedPass(Exception? failure)
    {
        if (failure is not null)
        {
            Volatile.Write(ref _fault, failure);
        }

        Interlocked.Increment(ref _consecutiveFailures);
        Interlocked.Increment(ref _failureCount);
    }

    private static void BackOff(CancellationToken cancellationToken)
        => cancellationToken.WaitHandle.WaitOne(FailureBackoff);
}
