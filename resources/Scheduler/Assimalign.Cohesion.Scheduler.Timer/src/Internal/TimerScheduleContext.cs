using System;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Timer.Internal;

internal sealed class TimerScheduleContext : IScheduleContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimerScheduleContext"/> class.
    /// </summary>
    /// <param name="id">The unique identifier for the schedule.</param>
    /// <param name="name">The friendly name for the schedule.</param>
    /// <param name="description">The description of the schedule.</param>
    /// <param name="scheduledTime">The occurrence time currently being executed.</param>
    /// <param name="lastRunTime">The preceding occurrence time, or <see langword="null"/> for the first occurrence.</param>
    /// <param name="nextRunTime">The occurrence time that follows this occurrence.</param>
    public TimerScheduleContext(
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
