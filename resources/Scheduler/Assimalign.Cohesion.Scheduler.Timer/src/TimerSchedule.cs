using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Timer;

/// <summary>
/// Provides the occurrence loop for fixed-delay timer schedules.
/// </summary>
/// <typeparam name="TContext">The occurrence context type.</typeparam>
public abstract class TimerSchedule<TContext> : Schedule<TContext>
    where TContext : IScheduleContext
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a timer schedule.
    /// </summary>
    /// <param name="provider">The provider that owns the schedule.</param>
    /// <param name="dueTime">The delay before the first occurrence.</param>
    /// <param name="interval">The delay measured after each completed occurrence.</param>
    /// <param name="jobs">The jobs executed at each occurrence.</param>
    /// <param name="timeProvider">The clock and timer provider.</param>
    protected TimerSchedule(
        IScheduleProvider provider,
        TimeSpan dueTime,
        TimeSpan interval,
        IEnumerable<IScheduleJob> jobs,
        TimeProvider? timeProvider = null)
        : base(provider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(jobs);

        DueTime = dueTime;
        Interval = interval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        foreach (IScheduleJob job in jobs)
        {
            AddJob(job);
        }
    }

    /// <summary>Gets the delay before the first occurrence.</summary>
    public TimeSpan DueTime { get; }

    /// <summary>Gets the fixed delay after each completed occurrence.</summary>
    public TimeSpan Interval { get; }

    /// <inheritdoc />
    public sealed override async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DateTime occurrence = _timeProvider.GetLocalNow().DateTime + DueTime;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetNextRunTime(occurrence);

                TimeSpan delay = occurrence - _timeProvider.GetLocalNow().DateTime;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                SetNextRunTime(null);
                // Stopping the schedule prevents another occurrence from starting. An occurrence
                // already in progress is drained by the host and bounded by its shutdown grace.
                await ExecuteOccurrenceAsync(
                    occurrence,
                    CancellationToken.None,
                    cancellationToken).ConfigureAwait(false);
                occurrence = _timeProvider.GetLocalNow().DateTime + Interval;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetNextRunTime(null);
            SetStatus(ScheduleStatus.Stopped);
        }
    }
}
