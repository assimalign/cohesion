using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Timer.Internal;

internal sealed class DefaultTimerSchedule : TimerSchedule<TimerScheduleContext>
{
    internal DefaultTimerSchedule(
        IScheduleProvider provider,
        string? name,
        TimeSpan dueTime,
        TimeSpan interval,
        IEnumerable<IScheduleJob> jobs,
        TimeProvider? timeProvider)
        : base(provider, dueTime, interval, jobs, timeProvider)
    {
        Name = name;
    }

    protected override TimerScheduleContext CreateContext(
        DateTime scheduledTime,
        DateTime? lastRunTime,
        DateTime? nextRunTime)
    {
        return new TimerScheduleContext(
            Id,
            Name,
            Description,
            scheduledTime,
            lastRunTime,
            nextRunTime);
    }
}
