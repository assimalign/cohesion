using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

public abstract class Schedule<TContext> : ISchedule where TContext : IScheduleContext
{
    private readonly ConcurrentBag<IScheduleJob> _jobs;

    private ScheduleId _id;


    protected Schedule(IScheduleProvider manager)
    {
        _id = ScheduleId.New();
        _jobs = new ConcurrentBag<IScheduleJob>();
    }

    public ScheduleId Id => _id;

    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <inheritdoc />
    public abstract int Retries { get; }

    /// <inheritdoc />
    public abstract int[] RetryIntervals { get; }

    /// <inheritdoc />
    public abstract int RetryCount { get; }

    /// <inheritdoc />
    public abstract DateTime GetNextRunTime { get; }

    /// <inheritdoc />
    public abstract DateTime GetLastRuntTime { get; }

    public ScheduleStatus Status => throw new NotImplementedException();
    public virtual SchedulePriority Priority => throw new NotImplementedException();

    public IEnumerable<IScheduleJob> Jobs => _jobs;

    public DateTime NextRunTime => throw new NotImplementedException();

    public DateTime? LastRunTime => throw new NotImplementedException();


    public virtual async Task RunAsync(CancellationToken cancellationToken = default)
    {
        

    }


    protected abstract TContext CreateContext();
}
