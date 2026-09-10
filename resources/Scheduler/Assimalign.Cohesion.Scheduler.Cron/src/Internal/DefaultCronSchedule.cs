using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Cron;

internal sealed class DefaultCronSchedule : CronSchedule<CronScheduleContext>
{
    internal DefaultCronSchedule(
        IScheduleProvider provider,
        string? name,
        Crontab expression,
        IEnumerable<IScheduleJob> jobs,
        TimeProvider? timeProvider)
        : base(provider, expression, jobs, timeProvider)
    {
        Name = name;
    }

    protected override CronScheduleContext CreateContext(
        DateTime scheduledTime,
        DateTime? lastRunTime,
        DateTime? nextRunTime)
    {
        return new CronScheduleContext(
            Id,
            Name,
            Description,
            scheduledTime,
            lastRunTime,
            nextRunTime);
    }
}
