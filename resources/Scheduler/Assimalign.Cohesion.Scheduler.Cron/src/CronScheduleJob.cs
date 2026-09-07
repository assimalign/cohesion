using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Base class for jobs triggered by a cron schedule. The Scheduler area is not started yet
/// (design item 31 scaffolds only its filler application); this type keeps the dormant Cron
/// package aligned with the area root's <see cref="IScheduleJob"/> contract until the
/// Scheduler program defines its job model.
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
        _state = default;
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
