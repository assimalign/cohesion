using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Describes one executable schedule and the jobs invoked for each occurrence.
/// </summary>
public interface ISchedule
{
    /// <summary>
    /// A unique identifier for the schedule.
    /// </summary>
    ScheduleId Id { get; }

    /// <summary>
    /// A friendly name for the schedule.
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// A description of the schedule.
    /// </summary>
    string? Description { get; }

    /// <summary>
    /// Gets the number of retries allowed after a failed occurrence.
    /// </summary>
    int Retries { get; }

    /// <summary>
    /// Gets the retry delays, in milliseconds.
    /// </summary>
    int[] RetryIntervals { get; }

    /// <summary>
    /// Gets the number of retries used by the current occurrence.
    /// </summary>
    int RetryCount { get; }

    /// <summary>
    /// Gets the next scheduled occurrence, or <see langword="null"/> when none is pending.
    /// </summary>
    DateTime? NextRunTime { get; }

    /// <summary>
    /// Gets the most recently started occurrence, or <see langword="null"/> before the first run.
    /// </summary>
    DateTime? LastRunTime { get; }

    /// <summary>
    /// Gets the current schedule status.
    /// </summary>
    ScheduleStatus Status { get; }

    /// <summary>
    /// Gets the compatibility priority classification. The scheduler host does not use this
    /// value to guarantee occurrence ordering or thread affinity.
    /// </summary>
    SchedulePriority Priority { get; }

    /// <summary>
    /// Gets the jobs executed sequentially for each occurrence.
    /// </summary>
    IEnumerable<IScheduleJob> Jobs { get; }

    /// <summary>
    /// Runs the schedule until it completes or shutdown is requested.
    /// </summary>
    /// <param name="cancellationToken">
    /// Stops pending waits and prevents future occurrences. An occurrence already running drains
    /// within the owning host's shutdown budget.
    /// </param>
    /// <returns>A task representing the schedule lifetime.</returns>
    Task RunAsync(CancellationToken cancellationToken = default);
}
