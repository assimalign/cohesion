using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Cron;

/// <summary>
/// Provides the evaluation loop for schedules driven by a five-field cron expression.
/// </summary>
/// <typeparam name="TContext">The occurrence context type.</typeparam>
public abstract class CronSchedule<TContext> : Schedule<TContext>
    where TContext : IScheduleContext
{
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a cron schedule.
    /// </summary>
    /// <param name="provider">The provider that owns the schedule.</param>
    /// <param name="expression">The parsed five-field expression.</param>
    /// <param name="jobs">The jobs executed at each occurrence.</param>
    /// <param name="timeProvider">The clock and timer provider.</param>
    protected CronSchedule(
        IScheduleProvider provider,
        Crontab expression,
        IEnumerable<IScheduleJob> jobs,
        TimeProvider? timeProvider = null)
        : base(provider)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        Expression = expression;
        _timeProvider = timeProvider ?? TimeProvider.System;
        foreach (IScheduleJob job in jobs)
        {
            AddJob(job);
        }
    }

    /// <summary>
    /// Gets the parsed cron expression.
    /// </summary>
    public Crontab Expression { get; }

    /// <inheritdoc />
    public sealed override async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTime now = _timeProvider.GetLocalNow().DateTime;
                DateTime occurrence = Expression.GetDateTime(now);
                SetNextRunTime(occurrence);

                TimeSpan delay = occurrence - now;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                SetNextRunTime(Expression.GetDateTime(occurrence));
                // Stopping the schedule prevents another occurrence from starting. An occurrence
                // already in progress is drained by the host and bounded by its shutdown grace.
                await ExecuteOccurrenceAsync(
                    occurrence,
                    CancellationToken.None,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetNextRunTime(null);
            SetStatus(ScheduleStatus.Stopped);
        }
    }
}
