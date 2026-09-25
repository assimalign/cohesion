using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Cron;

/// <summary>
/// Base class for jobs triggered by a cron schedule.
/// </summary>
public abstract class CronScheduleJob : IScheduleJob
{
    private JobState _state;

    /// <summary>
    /// Initializes the job with its identifier and an optional display name.
    /// </summary>
    /// <param name="id">The job identifier.</param>
    /// <param name="name">The optional display name.</param>
    protected CronScheduleJob(JobId id, string? name = null)
    {
        Id = id;
        Name = name;
        _state = JobState.Enabled;
    }

    /// <inheritdoc/>
    public JobId Id { get; }

    /// <inheritdoc/>
    public string? Name { get; }

    /// <inheritdoc/>
    public JobState State => _state;

    /// <summary>
    /// Executes the job body for one cron occurrence.
    /// </summary>
    /// <param name="context">The schedule context of this occurrence.</param>
    /// <param name="cancellationToken">A token that cancels the occurrence.</param>
    protected abstract ValueTask ExecuteCoreAsync(IScheduleContext context, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public ValueTask ExecuteAsync(IScheduleContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteCoreAsync(context, cancellationToken);
    }

    /// <summary>
    /// Records the job state observed by the scheduler.
    /// </summary>
    /// <param name="state">The new state.</param>
    protected void SetState(JobState state)
    {
        _state = state;
    }
}
