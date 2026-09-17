using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Provides the common state and sequential job execution behavior for a schedule.
/// </summary>
/// <typeparam name="TContext">The occurrence context type.</typeparam>
public abstract class Schedule<TContext> : ISchedule
    where TContext : IScheduleContext
{
    private readonly object _sync = new();
    private readonly List<IScheduleJob> _jobs = [];
    private DateTime? _nextRunTime;
    private DateTime? _lastRunTime;
    private ScheduleStatus _status = ScheduleStatus.Idle;

    /// <summary>
    /// Initializes a schedule owned by the specified provider.
    /// </summary>
    /// <param name="provider">The provider that owns the schedule.</param>
    protected Schedule(IScheduleProvider provider)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Id = ScheduleId.New();
    }

    /// <inheritdoc />
    public ScheduleId Id { get; }

    /// <inheritdoc />
    public string? Name { get; protected set; }

    /// <inheritdoc />
    public string? Description { get; protected set; }

    /// <inheritdoc />
    public virtual int Retries => 0;

    /// <inheritdoc />
    public virtual int[] RetryIntervals => [];

    /// <inheritdoc />
    public virtual int RetryCount => 0;

    /// <inheritdoc />
    public DateTime? NextRunTime
    {
        get
        {
            lock (_sync)
            {
                return _nextRunTime;
            }
        }
    }

    /// <inheritdoc />
    public DateTime? LastRunTime
    {
        get
        {
            lock (_sync)
            {
                return _lastRunTime;
            }
        }
    }

    /// <inheritdoc />
    public ScheduleStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public virtual SchedulePriority Priority => SchedulePriority.Normal;

    /// <inheritdoc />
    public IEnumerable<IScheduleJob> Jobs
    {
        get
        {
            lock (_sync)
            {
                return _jobs.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets the provider that owns this schedule.
    /// </summary>
    protected IScheduleProvider Provider { get; }

    /// <inheritdoc />
    public abstract Task RunAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a job to the schedule.
    /// </summary>
    /// <param name="job">The job to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="job"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A job with the same identifier is already registered.</exception>
    protected void AddJob(IScheduleJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        lock (_sync)
        {
            for (int index = 0; index < _jobs.Count; index++)
            {
                if (_jobs[index].Id == job.Id)
                {
                    throw new InvalidOperationException(
                        $"Schedule '{Id}' already contains job '{job.Id}'.");
                }
            }

            _jobs.Add(job);
        }
    }

    /// <summary>
    /// Records the next occurrence exposed through the schedule context.
    /// </summary>
    /// <param name="nextRunTime">The next occurrence time.</param>
    protected void SetNextRunTime(DateTime? nextRunTime)
    {
        lock (_sync)
        {
            _nextRunTime = nextRunTime;
        }
    }

    /// <summary>
    /// Records a schedule lifecycle state outside an occurrence.
    /// </summary>
    /// <param name="status">The state to expose.</param>
    protected void SetStatus(ScheduleStatus status)
    {
        lock (_sync)
        {
            _status = status;
        }
    }

    /// <summary>
    /// Executes every enabled job once for the current occurrence.
    /// </summary>
    /// <param name="occurrence">The scheduled occurrence time.</param>
    /// <param name="cancellationToken">Signals that the occurrence must stop.</param>
    /// <returns>A task representing all jobs in the occurrence.</returns>
    protected async Task ExecuteOccurrenceAsync(
        DateTime occurrence,
        CancellationToken cancellationToken = default)
    {
        await ExecuteOccurrenceAsync(
            occurrence,
            cancellationToken,
            startCancellationToken: default).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts an occurrence only if its scheduling loop has not begun stopping, then executes
    /// every enabled job with the independently selected occurrence token.
    /// </summary>
    /// <param name="occurrence">The scheduled occurrence time.</param>
    /// <param name="cancellationToken">Signals that work in the active occurrence must stop.</param>
    /// <param name="startCancellationToken">Prevents a not-yet-started occurrence from beginning.</param>
    /// <returns>A task representing all jobs in the occurrence.</returns>
    protected async Task ExecuteOccurrenceAsync(
        DateTime occurrence,
        CancellationToken cancellationToken,
        CancellationToken startCancellationToken)
    {
        var startGate = new OccurrenceStartGate(startCancellationToken.IsCancellationRequested);
        using CancellationTokenRegistration registration = startCancellationToken.UnsafeRegister(
            static state => ((OccurrenceStartGate)state!).Cancel(),
            startGate);
        if (!startGate.TryStart(startCancellationToken))
        {
            throw new OperationCanceledException(startCancellationToken);
        }

        IScheduleJob[] jobs;
        DateTime? previousRunTime;
        DateTime? nextRunTime;
        lock (_sync)
        {
            previousRunTime = _lastRunTime;
            _lastRunTime = occurrence;
            _status = ScheduleStatus.Running;
            nextRunTime = _nextRunTime;
            jobs = _jobs.ToArray();
        }

        TContext context = CreateContext(occurrence, previousRunTime, nextRunTime);

        try
        {
            for (int index = 0; index < jobs.Length; index++)
            {
                IScheduleJob job = jobs[index];
                if (job.State is JobState.Disabled || !Provider.IsJobEnabled(Id, job.Id))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                await job.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _status = ScheduleStatus.Idle;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                _status = ScheduleStatus.Cancelled;
            }

            throw;
        }
        catch
        {
            lock (_sync)
            {
                _status = ScheduleStatus.Failed;
            }

            throw;
        }
    }

    /// <summary>
    /// Creates the context passed to jobs for the current occurrence.
    /// </summary>
    /// <returns>The occurrence context.</returns>
    protected abstract TContext CreateContext(
        DateTime scheduledTime,
        DateTime? lastRunTime,
        DateTime? nextRunTime);

    private sealed class OccurrenceStartGate(bool cancellationRequested)
    {
        private readonly object _sync = new();
        private bool _cancellationRequested = cancellationRequested;

        internal void Cancel()
        {
            lock (_sync)
            {
                _cancellationRequested = true;
            }
        }

        internal bool TryStart(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return !_cancellationRequested && !cancellationToken.IsCancellationRequested;
            }
        }
    }
}
