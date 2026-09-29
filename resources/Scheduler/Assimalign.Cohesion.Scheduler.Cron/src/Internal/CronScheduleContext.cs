using System;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Cron.Internal;

internal sealed class CronScheduleContext : IScheduleContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CronScheduleContext"/> class.
    /// </summary>
    /// <param name="id">The identifier of the schedule that is running.</param>
    /// <param name="name">The schedule name, or <see langword="null"/> when the schedule is unnamed.</param>
    /// <param name="description">The schedule description, or <see langword="null"/> when none was given.</param>
    /// <param name="scheduledTime">The time the current run was scheduled for.</param>
    /// <param name="lastRunTime">The time of the previous run, or <see langword="null"/> when the schedule has not run before.</param>
    /// <param name="nextRunTime">The time of the next run, or <see langword="null"/> when no further run is scheduled.</param>
    public CronScheduleContext(
        ScheduleId id,
        string? name,
        string? description,
        DateTime scheduledTime,
        DateTime? lastRunTime,
        DateTime? nextRunTime)
    {
        Id = id;
        Name = name;
        Description = description;
        ScheduledTime = scheduledTime;
        LastRunTime = lastRunTime;
        NextRunTime = nextRunTime;
    }

    public ScheduleId Id { get; }

    public string? Name { get; }

    public string? Description { get; }

    public DateTime ScheduledTime { get; }

    public DateTime? LastRunTime { get; }

    public DateTime? NextRunTime { get; }
}
