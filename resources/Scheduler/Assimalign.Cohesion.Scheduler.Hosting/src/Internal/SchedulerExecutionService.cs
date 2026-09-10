using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Scheduler;

namespace Assimalign.Cohesion.Scheduler.Hosting;

internal sealed class SchedulerExecutionService(
    IReadOnlyList<ISchedule> schedules) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (schedules.Count == 0)
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
        var tasks = new Task[schedules.Count];
        var initializedTaskCount = 0;
        try
        {
            for (int index = 0; index < schedules.Count; index++)
            {
                tasks[index] = schedules[index].RunAsync(siblingCancellation.Token)
                    ?? throw new InvalidOperationException(
                        $"Schedule '{schedules[index].Name ?? schedules[index].Id.ToString()}' returned a null task.");
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
            ISchedule completedSchedule = schedules[scheduleIndex];
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
