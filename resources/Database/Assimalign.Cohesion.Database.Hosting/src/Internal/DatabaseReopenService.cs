using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

/// <summary>
/// The application's reopen of offline databases (owner decision 22 of 2026-10-06): while the
/// application runs, a database an engine reports offline is reopened through the engine's own
/// offline-reopen path, <see cref="DatabaseEngine.OpenDatabaseAsync"/>, with exponential backoff
/// and jitter, until the reopen succeeds or the application stops.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it reopens.</b> The service polls every engine of the application's frozen registry
/// for <see cref="DatabaseEngine.OfflineDatabases"/>, at most once a second (or once per
/// <see cref="DatabaseApplicationOptions.ReopenInitialDelay"/> when that is shorter). A database it
/// finds is reopened after a random delay between half the initial delay and the initial delay.
/// The engine's reopen closes the offline instance, which writes nothing, and opens the database
/// again from its files; the open's recovery reads the journal and decides every commit that was
/// not confirmed (#1243), the same path a caller of <c>OpenDatabaseAsync</c> takes.
/// </para>
/// <para>
/// <b>What it never reopens.</b> A database the engine stops listing before the first attempt was
/// reopened by someone else, dropped, or closed by its holder (owner decision 33: the engine
/// forgets it), and is left alone. A reopen that reports the database missing
/// (<see cref="DatabaseNotFoundException"/>) means it was dropped, and the service stops there. A
/// disposed engine's databases are left alone.
/// </para>
/// <para>
/// <b>Backoff.</b> After the n-th failed attempt the service waits a random time between half and
/// all of <c>min(maximum, initial × 2^n)</c>, so a reopen that keeps failing is retried less and
/// less often, and two databases that failed together do not retry in lockstep. A failed reopen
/// leaves the database closed and the engine no longer lists it, so the service keeps it as its own
/// until a reopen succeeds: health stays unhealthy for it all along. Until then each poll also
/// checks, off the loop's thread, whether someone else opened the database again, online, and
/// then lets it go, so health turns healthy without waiting out the backoff. RavenDB's database
/// landlord retries a database whose start failed the same way, after a randomized delay
/// (<c>src/Raven.Server/Documents/DatabasesLandlord.cs:1504-1510</c>), and drops a faulted load so
/// the next access tries again (<c>:831-835</c>).
/// </para>
/// <para>
/// <b>A database that flaps keeps its backoff.</b> A database found offline again soon after a
/// successful reopen does not start over at the initial delay: its first attempt waits the step
/// after the last one its previous episode reached, so a fault the reopen does not clear (a version
/// purge, a page write-back) is reopened less and less often instead of every few seconds, each time
/// with a full recovery under the engine's lock. "Soon" is the carry-over window
/// (<see cref="GetCarryOverWindow"/>, owner decision 48): the engine's
/// <see cref="DatabaseEngine.WorkerFailureWindow"/> plus the backoff step the episode reached, since
/// a worker gives up on a database no sooner than that window after its first failure, and never
/// shorter than <see cref="DatabaseApplicationOptions.ReopenMaximumDelay"/>, which covers a device
/// failure that takes the database offline again at once.
/// </para>
/// <para>
/// <b>Databases are reopened side by side.</b> Each database's attempt, and each check, runs on
/// the thread pool, and the loop goes on without waiting for it: an attempt that hangs (a recovery
/// stuck in its device) holds back that database only, and the loop keeps finding and reopening
/// the others. One attempt or check per database runs at a time.
/// </para>
/// <para>
/// <b>Stop never waits for it.</b> Every wait observes the stop signal, and an attempt or a check
/// is awaited until that signal only: a stop during a backoff returns at once, and a stop during
/// an attempt returns without it. The attempt then finishes on its own (an open runs to its end
/// under the engine's lock, and the engine's disposal waits for that lock), and its outcome is
/// still written to the event source.
/// </para>
/// <para>
/// Every finding, attempt and outcome is written to the <c>Assimalign.Cohesion.Database.Hosting</c>
/// event source (<see cref="DatabaseHostingEventSource"/>), which an application's logging receives
/// through <c>Assimalign.Cohesion.Logging.EventSource</c>.
/// </para>
/// </remarks>
internal sealed class DatabaseReopenService : IHostService, IAsyncDisposable
{
    // How often the engines are polled at most: an offline database is found within a second.
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<DatabaseEngine> _engines;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly TimeSpan _poll;

    // Guards everything below. Health reads a snapshot.
    private readonly object _sync = new();
    private readonly List<OfflineDatabase> _offline = [];
    private readonly List<Task> _operations = [];
    private readonly List<ReopenedDatabase> _reopened = [];
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;

    /// <summary>
    /// Initializes the service over the application's frozen engine registry.
    /// </summary>
    /// <param name="engines">The engines to supervise.</param>
    /// <param name="initialDelay">The first step of the backoff; validated by the builder.</param>
    /// <param name="maximumDelay">The longest step of the backoff; validated by the builder.</param>
    internal DatabaseReopenService(IReadOnlyList<DatabaseEngine> engines, TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        _engines = engines;
        _initialDelay = initialDelay;
        _maximumDelay = maximumDelay;
        _poll = initialDelay < _pollInterval ? initialDelay : _pollInterval;
        Id = ServiceId.New();
    }

    /// <inheritdoc />
    public ServiceId Id { get; }

    /// <summary>
    /// Gets or sets the reopen the service runs: the engine's <see cref="DatabaseEngine.OpenDatabaseAsync"/>.
    /// Internal, set before the application starts: this assembly's tests wrap it to fail attempts
    /// and to count them (the hook is on the type that makes the call, <c>database-area.md</c>).
    /// </summary>
    internal Func<DatabaseEngine, DatabaseName, CancellationToken, ValueTask> Reopen { get; set; } = OpenAsync;

    /// <summary>
    /// Starts the reopen loop and returns at once. Idempotent; the application starts it once.
    /// </summary>
    /// <param name="cancellationToken">Not observed: starting does no work.</param>
    /// <returns>A completed task.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping is null)
            {
                var stopping = new CancellationTokenSource();
                _stopping = stopping;
                _loop = Task.Run(() => RunAsync(stopping.Token), CancellationToken.None);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the reopen loop: signals it and waits for it to end, which it does at once, in a
    /// backoff or in an attempt alike (the remarks).
    /// </summary>
    /// <param name="cancellationToken">Ends the wait for the loop.</param>
    /// <returns>A task that completes once the loop ended, or the token was signaled.</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? stopping;
        Task loop;
        lock (_sync)
        {
            stopping = _stopping;
            loop = _loop;
        }

        if (stopping is null)
        {
            return;
        }

        // Outside the lock: the loop's continuations may run on this thread as the signal fires.
        stopping.Cancel();
        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's stop budget ran out; the loop ends on its own.
        }
    }

    /// <summary>
    /// Stops the loop if it runs and releases the stop signal.
    /// </summary>
    /// <returns>A task that completes once the loop ended.</returns>
    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? stopping;
        Task loop;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stopping = _stopping;
            loop = _loop;
        }

        stopping?.Cancel();
        await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        stopping?.Dispose();
    }

    /// <summary>
    /// Gets the databases the service is reopening: found offline and not open again yet. A
    /// database whose reopen failed is listed though its engine no longer lists it, since the
    /// failed reopen left it closed.
    /// </summary>
    /// <returns>A point-in-time snapshot.</returns>
    internal IReadOnlyList<ReopenState> GetPendingReopens()
    {
        lock (_sync)
        {
            var states = new ReopenState[_offline.Count];
            for (int index = 0; index < states.Length; index++)
            {
                var entry = _offline[index];
                states[index] = new ReopenState(entry.Engine, entry.Name, entry.Cause, entry.Attempts, entry.LastFailure, entry.NextDelay);
            }

            return states;
        }
    }

    /// <summary>
    /// Gets the backoff step after <paramref name="failedAttempts"/> failed attempts:
    /// <c>min(maximum, initial × 2^failedAttempts)</c>.
    /// </summary>
    /// <param name="failedAttempts">
    /// The failed attempts so far; zero before the first attempt. A database that went offline
    /// again soon after a reopen counts the attempts of its earlier episodes too.
    /// </param>
    /// <returns>The step; the delay is a random time between half the step and the step.</returns>
    internal TimeSpan GetBackoffStep(int failedAttempts)
    {
        double step = _initialDelay.Ticks * Math.Pow(2, Math.Clamp(failedAttempts, 0, 62));
        return step >= _maximumDelay.Ticks ? _maximumDelay : TimeSpan.FromTicks((long)step);
    }

    /// <summary>
    /// Gets how long the service remembers a successful reopen of a database of
    /// <paramref name="engine"/> whose episodes reached <paramref name="level"/> backoff steps: a
    /// database found offline again within it carries its backoff over (owner decision 48 of
    /// 2026-10-08).
    /// </summary>
    /// <param name="engine">The database's engine.</param>
    /// <param name="level">The backoff steps the database's episodes reached, its next episode's first.</param>
    /// <returns>
    /// The engine's <see cref="DatabaseEngine.WorkerFailureWindow"/> plus the step at
    /// <paramref name="level"/>, or the maximum delay when that is longer.
    /// </returns>
    /// <remarks>
    /// A worker's failures take a database offline no sooner than the engine's window after their
    /// first failed pass (owner decision 42), so a window of the maximum delay alone, a minute by
    /// default, ended before a worker could give up on a reopened database again (100 s at the
    /// engines' defaults), and every such episode started over at the initial delay. The step adds
    /// the slack for the worker to fail again after the reopen and for the service to find the
    /// database, growing with each episode as the backoff does. The maximum delay stays the floor:
    /// a device failure takes a database offline at once, whatever the engine's window.
    /// </remarks>
    internal TimeSpan GetCarryOverWindow(DatabaseEngine engine, int level)
    {
        // Both terms are at most int.MaxValue milliseconds (DatabaseEngine.MaximumWorkerFailureWindow
        // and DatabaseApplicationOptions.MaximumReopenDelay), so the sum cannot overflow.
        TimeSpan window = engine.WorkerFailureWindow + GetBackoffStep(level);
        return window > _maximumDelay ? window : _maximumDelay;
    }

    // The engine's own offline-reopen path; what the reopen returns is the engine's to track.
    private static async ValueTask OpenAsync(DatabaseEngine engine, DatabaseName name, CancellationToken cancellationToken)
        => await engine.OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);

    private async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = Pass(stopping);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The loop never ends before the application stops: a failure of its own is
                // written, and the next poll tries again.
                DatabaseHostingEventSource.Log.ReopenLoopFailed(exception);
                wait = _poll;
            }

            try
            {
                await Task.Delay(wait, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Each attempt and check in flight awaits the stop signal, so each ends at once; the stop
        // signal is released only after them.
        Task[] operations;
        lock (_sync)
        {
            operations = [.. _operations];
        }

        await Task.WhenAll(operations).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Finds the offline databases, starts the attempts that are due and the checks that are
    /// needed, and returns how long to wait for the next poll or attempt. Never waits on an
    /// engine: every engine call it makes reads a published snapshot.
    /// </summary>
    private TimeSpan Pass(CancellationToken stopping)
    {
        Discover();

        TimeSpan wait = _poll;
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            foreach (var entry in _offline)
            {
                if (entry.Operation is not null)
                {
                    continue;
                }

                // A database a failed attempt left closed is checked for an open by someone else at
                // every poll; one that is due is attempted (after the check, when it needs one).
                bool check = entry.LastFailure is not null && !entry.Listed;
                bool attempt = entry.NextAttemptAt <= now;
                if (check || attempt)
                {
                    var operation = Task.Run(() => RunEntryAsync(entry, check, attempt, stopping), CancellationToken.None);
                    entry.Operation = operation;
                    _operations.Add(operation);
                }

                if (!attempt)
                {
                    TimeSpan until = Stopwatch.GetElapsedTime(now, entry.NextAttemptAt);
                    if (until < wait)
                    {
                        wait = until;
                    }
                }
            }
        }

        return wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait;
    }

    /// <summary>
    /// Records every database an engine lists offline, and lets go of a database whose engine no
    /// longer lists it before any attempt: someone else reopened, dropped or closed it.
    /// </summary>
    private void Discover()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            // A reopen remembered past its carry-over window no longer carries its backoff over.
            _reopened.RemoveAll(reopened => Stopwatch.GetElapsedTime(reopened.At, now) > reopened.Window);
        }

        foreach (DatabaseEngine engine in _engines)
        {
            IReadOnlyList<DatabaseName>? listed = null;
            try
            {
                if (engine.State != EngineState.Disposed)
                {
                    listed = engine.OfflineDatabases;
                }
            }
            catch (ObjectDisposedException)
            {
                // Disposed while it was read.
            }

            List<OfflineDatabase>? letGo = null;
            lock (_sync)
            {
                foreach (var entry in _offline)
                {
                    if (!ReferenceEquals(entry.Engine, engine))
                    {
                        continue;
                    }

                    entry.Listed = listed is not null && Contains(listed, entry.Name);
                    if (entry.Operation is not null)
                    {
                        // Its attempt or check settles it.
                        continue;
                    }

                    if (listed is null)
                    {
                        (letGo ??= []).Add(entry);
                    }
                    else if (entry.LastFailure is null && !entry.Listed)
                    {
                        // Never attempted, and no longer offline: not this service's to reopen.
                        (letGo ??= []).Add(entry);
                    }
                }

                if (letGo is not null)
                {
                    foreach (var entry in letGo)
                    {
                        _offline.Remove(entry);
                    }
                }
            }

            if (letGo is not null)
            {
                foreach (var entry in letGo)
                {
                    DatabaseHostingEventSource.Log.ReopenAbandoned(engine, entry.Name, listed is null
                        ? "its engine was disposed"
                        : "its engine no longer lists it offline: it was reopened, dropped or closed by its holder");
                }
            }

            if (listed is null)
            {
                continue;
            }

            foreach (DatabaseName name in listed)
            {
                bool found;
                lock (_sync)
                {
                    found = Find(engine, name) is not null;
                }

                if (found)
                {
                    continue;
                }

                StorageOfflineCause? cause = GetCause(engine, name);
                OfflineDatabase entry;
                lock (_sync)
                {
                    // Found offline again soon after a reopen: the backoff goes on from where the
                    // last episode left it (the remarks).
                    int level = 0;
                    if (FindReopened(engine, name) is { } reopened)
                    {
                        level = reopened.Level;
                        _reopened.Remove(reopened);
                    }

                    TimeSpan delay = Jitter(GetBackoffStep(level));
                    entry = new OfflineDatabase(engine, name, cause, level)
                    {
                        Listed = true,
                        NextAttemptAt = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency),
                        NextDelay = delay,
                    };
                    _offline.Add(entry);
                }

                DatabaseHostingEventSource.Log.OfflineDatabaseFound(engine, name, cause, entry.NextDelay);
            }
        }
    }

    /// <summary>
    /// Runs one database's check and attempt, off the loop's thread, and records the outcome.
    /// </summary>
    private async Task RunEntryAsync(OfflineDatabase entry, bool check, bool attempt, CancellationToken stopping)
    {
        try
        {
            if (check && await IsOpenAgainAsync(entry, stopping).ConfigureAwait(false))
            {
                Remove(entry);
                DatabaseHostingEventSource.Log.ReopenAbandoned(entry.Engine, entry.Name, "it was reopened elsewhere");
                return;
            }

            if (attempt)
            {
                await AttemptAsync(entry, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The application stops.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DatabaseHostingEventSource.Log.ReopenLoopFailed(exception);
        }
        finally
        {
            lock (_sync)
            {
                _operations.Remove(entry.Operation!);
                entry.Operation = null;
            }
        }
    }

    /// <summary>
    /// Checks whether a database a failed attempt left closed is open again, and online, because
    /// someone else reopened it: the engine holds it open and does not list it offline. The check
    /// waits for the engine's registry lock, which an open holds through its recovery, so it runs
    /// on a thread of its own and is awaited until the stop signal only.
    /// </summary>
    private static async Task<bool> IsOpenAgainAsync(OfflineDatabase entry, CancellationToken stopping)
    {
        var check = Task.Run(() => entry.Engine.TryGetDatabase(entry.Name, out _) && !Contains(entry.Engine.OfflineDatabases, entry.Name), CancellationToken.None);
        try
        {
            return await check.WaitAsync(stopping).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Its engine was disposed: the next poll lets the database go.
            return false;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The check finishes on its own; its failure, if any, is observed and dropped.
            _ = check.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// Runs one reopen attempt and records its outcome.
    /// </summary>
    private async Task AttemptAsync(OfflineDatabase entry, CancellationToken stopping)
    {
        int attempt;
        lock (_sync)
        {
            attempt = ++entry.Attempts;
        }

        DatabaseHostingEventSource.Log.ReopenAttempted(entry.Engine, entry.Name, attempt);

        // On the thread pool, awaited until the stop signal only: an engine's open runs its
        // recovery synchronously under its lock, and a stop must not wait for it.
        var reopen = Task.Run(() => Reopen(entry.Engine, entry.Name, stopping).AsTask(), CancellationToken.None);
        try
        {
            await reopen.WaitAsync(stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // The application stops: the attempt finishes on its own, and its outcome is written.
            _ = reopen.ContinueWith(
                static (task, state) => WriteAbandonedOutcome(task, (OfflineDatabase)state!),
                entry,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (DatabaseNotFoundException)
        {
            // Dropped meanwhile: never reopened.
            Remove(entry);
            DatabaseHostingEventSource.Log.ReopenAbandoned(entry.Engine, entry.Name, "it was dropped");
            return;
        }
        catch (ObjectDisposedException) when (IsDisposed(entry.Engine))
        {
            Remove(entry);
            DatabaseHostingEventSource.Log.ReopenAbandoned(entry.Engine, entry.Name, "its engine was disposed");
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TimeSpan delay = Jitter(GetBackoffStep(entry.Level + attempt));
            lock (_sync)
            {
                entry.LastFailure = exception;
                entry.NextDelay = delay;
                entry.NextAttemptAt = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
            }

            DatabaseHostingEventSource.Log.ReopenFailed(entry.Engine, entry.Name, attempt, exception, delay);
            return;
        }

        lock (_sync)
        {
            _offline.Remove(entry);

            // Remembered for the carry-over window: a database that goes offline again within it
            // goes on from the step after the last one this episode reached (owner decision 48).
            if (FindReopened(entry.Engine, entry.Name) is { } earlier)
            {
                _reopened.Remove(earlier);
            }

            int level = entry.Level + attempt;
            _reopened.Add(new ReopenedDatabase(entry.Engine, entry.Name, level, Stopwatch.GetTimestamp(), GetCarryOverWindow(entry.Engine, level)));
        }

        DatabaseHostingEventSource.Log.ReopenSucceeded(entry.Engine, entry.Name, attempt);
    }

    // The outcome of an attempt the application's stop left running.
    private static void WriteAbandonedOutcome(Task reopen, OfflineDatabase entry)
    {
        if (reopen.IsCompletedSuccessfully)
        {
            DatabaseHostingEventSource.Log.ReopenSucceeded(entry.Engine, entry.Name, entry.Attempts);
        }
        else if (reopen.Exception?.GetBaseException() is { } failure)
        {
            DatabaseHostingEventSource.Log.ReopenAbandoned(entry.Engine, entry.Name,
                $"the application stopped, and its last attempt failed ({failure.GetType().Name})");
        }
    }

    private void Remove(OfflineDatabase entry)
    {
        lock (_sync)
        {
            _offline.Remove(entry);
        }
    }

    // Under the lock.
    private OfflineDatabase? Find(DatabaseEngine engine, string name)
    {
        foreach (var entry in _offline)
        {
            if (ReferenceEquals(entry.Engine, engine) && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    // Under the lock.
    private ReopenedDatabase? FindReopened(DatabaseEngine engine, string name)
    {
        foreach (var reopened in _reopened)
        {
            if (ReferenceEquals(reopened.Engine, engine) && string.Equals(reopened.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return reopened;
            }
        }

        return null;
    }

    private static bool Contains(IReadOnlyList<DatabaseName> names, string name)
    {
        for (int index = 0; index < names.Count; index++)
        {
            if (string.Equals(names[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets what took a database offline: the cause of the storage error the engine reports for it
    /// (<see cref="DatabaseEngine.GetOfflineError"/>), or null when the engine reports none (the
    /// database is online again, not open, or the engine was disposed meanwhile).
    /// </summary>
    internal static StorageOfflineCause? GetCause(DatabaseEngine engine, DatabaseName name)
    {
        try
        {
            return engine.GetOfflineError(name)?.Cause;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    // The engine base folds its state from its own disposal flag, so reading it never throws.
    private static bool IsDisposed(DatabaseEngine engine) => engine.State == EngineState.Disposed;

    // A random delay between half the step and the step.
    private static TimeSpan Jitter(TimeSpan step)
        => TimeSpan.FromTicks(step.Ticks / 2 + (long)(Random.Shared.NextDouble() * (step.Ticks - step.Ticks / 2)));

    /// <summary>
    /// A database the service found offline: what took it offline, its reopen attempts, and the
    /// attempt or check running for it.
    /// </summary>
    private sealed class OfflineDatabase
    {
        public OfflineDatabase(DatabaseEngine engine, string name, StorageOfflineCause? cause, int level)
        {
            Engine = engine;
            Name = name;
            Cause = cause;
            Level = level;
        }

        public DatabaseEngine Engine { get; }

        public string Name { get; }

        public StorageOfflineCause? Cause { get; }

        // The backoff steps earlier episodes reached: zero unless the database went offline again
        // soon after a reopen.
        public int Level { get; }

        public int Attempts { get; set; }

        public Exception? LastFailure { get; set; }

        public long NextAttemptAt { get; set; }

        public TimeSpan NextDelay { get; set; }

        // Whether its engine listed it offline at the last poll.
        public bool Listed { get; set; }

        // The attempt or check running for it, or null.
        public Task? Operation { get; set; }
    }

    /// <summary>
    /// A database the service reopened, remembered for its carry-over window
    /// (<see cref="GetCarryOverWindow"/>) so that a database that goes offline again keeps its
    /// backoff.
    /// </summary>
    private sealed record ReopenedDatabase(DatabaseEngine Engine, string Name, int Level, long At, TimeSpan Window);
}
