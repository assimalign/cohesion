using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler;

using Internal;

public abstract class Schedule<TContext> : ISchedule where TContext : IScheduleContext
{
    private readonly ScheduleTaskScheduler _taskScheduler;
    private readonly ConcurrentBag<IScheduleJob> _jobs;
    //private readonly IScheduleStateHandler _state;

    private ScheduleId _id;
    private Task? _task;
    private CancellationTokenSource? _stoppingToken;

    private volatile ScheduleStatus _state;


    protected Schedule(IScheduleProvider manager)
    {
        _id = ScheduleId.New();
        _jobs = new ConcurrentBag<IScheduleJob>();
        _lock = new Lock();
    }

    public ScheduleId Id => _id;

    public string? Name { get; set; }

    public string? Description { get; set; }
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