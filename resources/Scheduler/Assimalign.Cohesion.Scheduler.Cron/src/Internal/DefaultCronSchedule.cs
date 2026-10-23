using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Cron.Internal;

[global::System.Diagnostics.DebuggerDisplay("DefaultCronSchedule {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly DefaultCronSchedule _target;

        public DebugView(DefaultCronSchedule value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Scheduler.Cron.Crontab Expression => _target.Expression;
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
