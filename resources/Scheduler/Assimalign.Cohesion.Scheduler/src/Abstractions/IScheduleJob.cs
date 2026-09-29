using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Defines a unit of work executed by a schedule provider.
/// </summary>
public interface IScheduleJob
{
    /// <summary>
    /// A unique identifier for the job.
    /// </summary>
    JobId Id { get; }

    /// <summary>
    /// A friendly name for the job.
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// Gets whether the job is enabled for execution.
    /// </summary>
    JobState State { get; }

    /// <summary>
    /// Executes the job for one schedule occurrence.
    /// </summary>
    /// <param name="context">The occurrence context.</param>
    /// <param name="cancellationToken">Signals that the occurrence must stop.</param>
    /// <returns>A value task representing the job execution.</returns>
    ValueTask ExecuteAsync(IScheduleContext context, CancellationToken cancellationToken = default);
}
