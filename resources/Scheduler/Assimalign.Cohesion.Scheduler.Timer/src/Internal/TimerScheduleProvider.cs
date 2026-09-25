using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Timer.Internal;

internal sealed class TimerScheduleProvider : IScheduleProvider
{
    private readonly object _sync = new();
    private readonly ISchedule _schedule;
    private readonly HashSet<JobId> _disabledJobs = [];

    internal TimerScheduleProvider(
        string? name,
        TimeSpan dueTime,
        TimeSpan interval,
        IEnumerable<IScheduleJob> jobs,
        TimeProvider? timeProvider)
    {
        _schedule = new DefaultTimerSchedule(
            this,
            name,
            dueTime,
            interval,
            jobs,
            timeProvider);
    }

    public ISchedule GetSchedule(ScheduleId id) =>
        id == _schedule.Id
            ? _schedule
            : throw new KeyNotFoundException($"Schedule '{id}' is not registered.");

    public IEnumerable<ISchedule> GetSchedules() => [_schedule];

    public void DisableJob(ScheduleId scheduleId, JobId jobId)
    {
        ValidateJob(scheduleId, jobId);
        lock (_sync)
        {
            _disabledJobs.Add(jobId);
        }
    }

    public void EnableJob(ScheduleId scheduleId, JobId jobId)
    {
        ValidateJob(scheduleId, jobId);
        lock (_sync)
        {
            _disabledJobs.Remove(jobId);
        }
    }

    public bool IsJobEnabled(ScheduleId scheduleId, JobId jobId)
    {
        ValidateJob(scheduleId, jobId);
        lock (_sync)
        {
            return !_disabledJobs.Contains(jobId);
        }
    }

    private void ValidateJob(ScheduleId scheduleId, JobId jobId)
    {
        ISchedule schedule = GetSchedule(scheduleId);
        if (!schedule.Jobs.Any(job => job.Id == jobId))
        {
            throw new KeyNotFoundException(
                $"Job '{jobId}' is not registered under schedule '{scheduleId}'.");
        }
    }
}
