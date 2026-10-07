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
/// offline-reopen path, <see cref="IDatabaseEngine.OpenDatabaseAsync"/>, with exponential backoff
/// and jitter, until the reopen succeeds or the application stops.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it reopens.</b> The service polls every engine of the application's frozen registry
/// for <see cref="IDatabaseEngine.OfflineDatabases"/>, at most once a second (or once per
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
/// until a reopen succeeds: health stays unhealthy for it all along. RavenDB's database landlord
/// retries a database whose start failed the same way, after a randomized delay
/// (<c>src/Raven.Server/Documents/DatabasesLandlord.cs:1504-1510</c>), and drops a faulted load so
/// the next access tries again (<c>:831-835</c>).
/// </para>
/// <para>
/// <b>Stop never waits for it.</b> Every wait observes the stop signal, and an attempt runs on the
/// thread pool and is awaited until the stop signal only: a stop during a backoff returns at once,
/// and a stop during an attempt returns without it. The attempt then finishes on its own (an open
/// runs to its end under the engine's lock, and the engine's disposal waits for that lock), and its
/// outcome is still written to the event source.
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

    private readonly IReadOnlyList<IDatabaseEngine> _engines;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;
    private readonly TimeSpan _poll;

    // Guards everything below. The loop is the only writer; health reads a snapshot.
    private readonly object _sync = new();
    private readonly List<OfflineDatabase> _offline = [];
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;

    /// <summary>
    /// Initializes the service over the application's frozen engine registry.
    /// </summary>
    /// <param name="engines">The engines to supervise.</param>
    /// <param name="initialDelay">The first step of the backoff; validated by the builder.</param>
    /// <param name="maximumDelay">The longest step of the backoff; validated by the builder.</param>
    internal DatabaseReopenService(IReadOnlyList<IDatabaseEngine> engines, TimeSpan initialDelay, TimeSpan maximumDelay)
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
    /// Gets or sets the reopen the service runs: the engine's <see cref="IDatabaseEngine.OpenDatabaseAsync"/>.
    /// Internal, set before the application starts: this assembly's tests wrap it to fail attempts
    /// and to count them (the hook is on the type that makes the call, <c>database-area.md</c>).
    /// </summary>
    internal Func<IDatabaseEngine, DatabaseName, CancellationToken, ValueTask> Reopen { get; set; } = OpenAsync;

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
    /// <param name="failedAttempts">The failed attempts so far; zero before the first attempt.</param>
    /// <returns>The step; the delay is a random time between half the step and the step.</returns>
    internal TimeSpan GetBackoffStep(int failedAttempts)
    {
        double step = _initialDelay.Ticks * Math.Pow(2, Math.Min(failedAttempts, 62));
        return step >= _maximumDelay.Ticks ? _maximumDelay : TimeSpan.FromTicks((long)step);
    }

    // The engine's own offline-reopen path; what the reopen returns is the engine's to track.
    private static async ValueTask OpenAsync(IDatabaseEngine engine, DatabaseName name, CancellationToken cancellationToken)
        => await engine.OpenDatabaseAsync(name, cancellationToken).ConfigureAwait(false);

    private async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await PassAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
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
                return;
            }
        }
    }

    /// <summary>
    /// Finds the offline databases, runs the attempts that are due, and returns how long to wait
    /// for the next poll or attempt.
    /// </summary>
    private async Task<TimeSpan> PassAsync(CancellationToken stopping)
    {
        Discover();

        OfflineDatabase[] due;
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            due = _offline.FindAll(entry => entry.NextAttemptAt <= now).ToArray();
        }

        foreach (var entry in due)
        {
            stopping.ThrowIfCancellationRequested();
            await AttemptAsync(entry, stopping).ConfigureAwait(false);
        }

        // Until the next poll, or the next attempt when it is sooner.
        TimeSpan wait = _poll;
        now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            foreach (var entry in _offline)
            {
                TimeSpan until = entry.NextAttemptAt <= now ? TimeSpan.Zero : Stopwatch.GetElapsedTime(now, entry.NextAttemptAt);
                if (until < wait)
                {
                    wait = until;
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
        foreach (IDatabaseEngine engine in _engines)
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

                    if (listed is null)
                    {
                        (letGo ??= []).Add(entry);
                    }
                    else if (entry.LastFailure is null && !Contains(listed, entry.Name))
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
                TimeSpan delay = Jitter(GetBackoffStep(0));
                var entry = new OfflineDatabase(engine, name, cause)
                {
                    NextAttemptAt = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency),
                    NextDelay = delay,
                };

                lock (_sync)
                {
                    _offline.Add(entry);
                }

                DatabaseHostingEventSource.Log.OfflineDatabaseFound(engine, name, cause, delay);
            }
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
            TimeSpan delay = Jitter(GetBackoffStep(attempt));
            lock (_sync)
            {
                entry.LastFailure = exception;
                entry.NextDelay = delay;
                entry.NextAttemptAt = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
            }

            DatabaseHostingEventSource.Log.ReopenFailed(entry.Engine, entry.Name, attempt, exception, delay);
            return;
        }

        Remove(entry);
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
    private OfflineDatabase? Find(IDatabaseEngine engine, string name)
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
    /// Gets what took a database offline, when its engine says: an engine of the root base reports
    /// the storage's cause (<see cref="DatabaseEngine.GetOfflineError"/>).
    /// </summary>
    internal static StorageOfflineCause? GetCause(IDatabaseEngine engine, DatabaseName name)
    {
        try
        {
            return engine is DatabaseEngine typed ? typed.GetOfflineError(name)?.Cause : null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static bool IsDisposed(IDatabaseEngine engine)
    {
        try
        {
            return engine.State == EngineState.Disposed;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    // A random delay between half the step and the step.
    private static TimeSpan Jitter(TimeSpan step)
        => TimeSpan.FromTicks(step.Ticks / 2 + (long)(Random.Shared.NextDouble() * (step.Ticks - step.Ticks / 2)));

    /// <summary>
    /// A database the service found offline: what took it offline, and its reopen attempts.
    /// </summary>
    private sealed class OfflineDatabase(IDatabaseEngine engine, string name, StorageOfflineCause? cause)
    {
        public IDatabaseEngine Engine { get; } = engine;

        public string Name { get; } = name;

        public StorageOfflineCause? Cause { get; } = cause;

        public int Attempts { get; set; }

        public Exception? LastFailure { get; set; }

        public long NextAttemptAt { get; set; }

        public TimeSpan NextDelay { get; set; }
    }
}
