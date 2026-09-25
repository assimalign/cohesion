using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Hosting.Internal;

internal sealed class SchedulerExecutionService : BackgroundService
{
    private readonly IReadOnlyList<ISchedule> _schedules;

    /// <summary>Initializes a new instance of the <see cref="SchedulerExecutionService"/> class.</summary>
    /// <param name="schedules">The schedules to run concurrently until shutdown is requested.</param>
    public SchedulerExecutionService(
        IReadOnlyList<ISchedule> schedules)
    {
        _schedules = schedules;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_schedules.Count == 0)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            return;
        }

        using var siblingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new Task[_schedules.Count];
        var initializedTaskCount = 0;
        try
        {
            for (int index = 0; index < _schedules.Count; index++)
            {
                tasks[index] = _schedules[index].RunAsync(siblingCancellation.Token)
                    ?? throw new InvalidOperationException(
                        $"Schedule '{_schedules[index].Name ?? _schedules[index].Id.ToString()}' returned a null task.");
                initializedTaskCount++;
            }
        }
        catch
        {
            siblingCancellation.Cancel();
            _ = ObserveTasksAsync(tasks, initializedTaskCount);
            throw;
        }

        Task firstCompleted = await Task.WhenAny(tasks).ConfigureAwait(false);
        if (firstCompleted.IsFaulted)
        {
            siblingCancellation.Cancel();
            _ = ObserveTasksAsync(tasks, tasks.Length);
            await firstCompleted.ConfigureAwait(false);
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            siblingCancellation.Cancel();
            _ = ObserveTasksAsync(tasks, tasks.Length);
            int scheduleIndex = Array.IndexOf(tasks, firstCompleted);
            ISchedule completedSchedule = _schedules[scheduleIndex];
            throw new InvalidOperationException(
                $"Schedule '{completedSchedule.Name ?? completedSchedule.Id.ToString()}' " +
                "completed before scheduler shutdown was requested.");
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task ObserveTasksAsync(Task[] tasks, int count)
    {
        try
        {
            await Task.WhenAll(tasks.AsMemory(0, count).ToArray()).ConfigureAwait(false);
        }
        catch
        {
            // The first failure is propagated by ExecuteAsync. Sibling failures are observed
            // here so cancelling their loops cannot create unobserved task exceptions.
        }
    }
}
