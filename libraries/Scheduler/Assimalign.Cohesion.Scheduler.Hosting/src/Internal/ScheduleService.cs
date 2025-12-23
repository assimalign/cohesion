
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Scheduler.Internal;

using Assimalign.Cohesion.Hosting;
using System.ComponentModel.DataAnnotations;
using System.Linq;

internal class ScheduleService : IHostService
{
    // private readonly ScheduleTaskScheduler _taskScheduler;
    private readonly ConcurrentBag<IScheduleJob> _jobs;
    private readonly IScheduleProvider _state;
    private readonly SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(1, 1);

    private Task? _task;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly List<ISchedule> _schedules;
    private readonly TimeProvider _timeProvider;



    public ScheduleService(ScheduleServiceOptions options)
    {

    }

    public ServiceId Id => throw new NotImplementedException();

    protected async Task RunAsync(CancellationToken cancellationToken = default)
    {
        /*
            Schedules will be responsible for managing their own state. The Schedule Service will be responsible for
            polling the schedules and executing them when their time comes.
         */
        if (_cancellationTokenSource is null)
        {
            throw new Exception("The service was not started.");
        }

        while (_cancellationTokenSource.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow();
            var next = _schedules
                .Where(schedule => schedule.Status == ScheduleStatus.Idle && schedule.NextRunTime <= now);

            if (next.Any())
            {
                await _semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                
                foreach (var schedule in next)
                {
                    try
                    {
                        if (schedule.Priority == SchedulePriority.LongRunning)
                        {
                            _ = Task.Factory.StartNew(async () =>
                            {
                                await schedule.RunAsync(cancellationToken).ConfigureAwait(false);

                            }, TaskCreationOptions.LongRunning);
                        }
                        else
                        {
                            var task = Task.Factory.StartNew(async () =>
                            {
                                await schedule.RunAsync(cancellationToken).ConfigureAwait(false);

                            }, TaskCreationOptions.DenyChildAttach)
                                .Unwrap();
                        }
                    }
                    catch (Exception exception)
                    {

                    }
                }
                _semaphoreSlim.Release();
            }
            else
            {
                _schedules.Min()
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Create linked token to allow cancelling executing task from provided token
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Execute all of ExecuteAsync asynchronously, and store the task we're executing so that we can wait for it later.
        _task = Task.Factory.StartNew(
            async () => await RunAsync(_cancellationTokenSource.Token),
            _cancellationTokenSource.Token,
            TaskCreationOptions.LongRunning,
            null);

        // Always return a completed task.  Any result from ExecuteAsync will be handled by the Host.
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Stop called without start
        if (_task is null)
        {
            return;
        }

        try
        {
            // Signal cancellation to the executing method
            _cancellationTokenSource!.Cancel();
        }
        finally
        {
            await _task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
