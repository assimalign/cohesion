using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Timer.Internal;

[global::System.Diagnostics.DebuggerDisplay("DefaultTimerSchedule {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly DefaultTimerSchedule _target;

        public DebugView(DefaultTimerSchedule value)
        {
            _target = value;
        }

        public global::System.TimeSpan DueTime => _target.DueTime;
        public global::System.TimeSpan Interval => _target.Interval;
        public global::Assimalign.Cohesion.Scheduler.ScheduleId Id => _target.Id;
        public string? Name => _target.Name;
        public string? Description => _target.Description;
        public int Retries => _target.Retries;
        public int[] RetryIntervals => _target.RetryIntervals;
        public int RetryCount => _target.RetryCount;
        public global::System.DateTime? NextRunTime => _target.NextRunTime;
        public global::System.DateTime? LastRunTime => _target.LastRunTime;
        public global::Assimalign.Cohesion.Scheduler.ScheduleStatus Status => _target.Status;
        public global::Assimalign.Cohesion.Scheduler.SchedulePriority Priority => _target.Priority;
        public global::Assimalign.Cohesion.Scheduler.IScheduleProvider Provider => _target.Provider;
    }
}
