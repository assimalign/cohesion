using System;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Cron;

internal sealed class CronScheduleContext(
    ScheduleId id,
    string? name,
    string? description,
    DateTime scheduledTime,
    DateTime? lastRunTime,
    DateTime? nextRunTime) : IScheduleContext
{
    public ScheduleId Id { get; } = id;

    public string? Name { get; } = name;

    public string? Description { get; } = description;

    public DateTime ScheduledTime { get; } = scheduledTime;

    public DateTime? LastRunTime { get; } = lastRunTime;

    public DateTime? NextRunTime { get; } = nextRunTime;
}
