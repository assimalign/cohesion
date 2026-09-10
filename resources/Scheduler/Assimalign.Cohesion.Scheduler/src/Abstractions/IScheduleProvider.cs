using System;
using System.Collections.Generic;
namespace Assimalign.Cohesion.Scheduler;

/// <summary>
/// Supplies schedules to a scheduler application and controls per-provider job enablement.
/// </summary>
public interface IScheduleProvider
{
    /// <summary>
    /// Gets one schedule by identifier.
    /// </summary>
    /// <param name="id">The schedule identifier.</param>
    /// <returns>The registered schedule.</returns>
    /// <exception cref="KeyNotFoundException">No schedule has the requested identifier.</exception>
    ISchedule GetSchedule(ScheduleId id);

    /// <summary>
    /// Gets an immutable snapshot of all schedules in the provider.
    /// </summary>
    /// <returns>The schedule snapshot.</returns>
    IEnumerable<ISchedule> GetSchedules();

    /// <summary>
    /// Disables a job under a specific schedule.
    /// </summary>
    /// <param name="scheduleId">The schedule identifier.</param>
    /// <param name="jobId">The job identifier.</param>
    /// <exception cref="KeyNotFoundException">The schedule or job is not registered.</exception>
    void DisableJob(ScheduleId scheduleId, JobId jobId);

    /// <summary>
    /// Enables a job under a specific schedule.
    /// </summary>
    /// <param name="scheduleId">The schedule identifier.</param>
    /// <param name="jobId">The job identifier.</param>
    /// <exception cref="KeyNotFoundException">The schedule or job is not registered.</exception>
    void EnableJob(ScheduleId scheduleId, JobId jobId);

    /// <summary>
    /// Determines whether a job is enabled by this provider.
    /// </summary>
    /// <param name="scheduleId">The schedule identifier.</param>
    /// <param name="jobId">The job identifier.</param>
    /// <returns><see langword="true"/> when the provider permits the job to run.</returns>
    bool IsJobEnabled(ScheduleId scheduleId, JobId jobId);
}
