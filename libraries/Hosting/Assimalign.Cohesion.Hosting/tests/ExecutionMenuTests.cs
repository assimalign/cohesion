using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

public class ExecutionMenuTests
{
    public const string DisplayPrefix = "Cohesion Test [Hosting] - Execution menu: ";

    [Fact(DisplayName = DisplayPrefix + "One host mixes dedicated-thread and pooled services, each on its chosen model")]
    public async Task Host_WithMixedExecutionModels_RunsEachServiceOnItsChosenModel()
    {
        // Arrange - a database-engine-style blocking flush worker on a dedicated thread...
        bool? flushOnPoolThread = null;
        var flushDrained = false;
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushWorker = new DelegateDedicatedThreadService(cancellationToken =>
        {
            flushOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            flushStarted.SetResult();
            cancellationToken.WaitHandle.WaitOne();
            flushDrained = true;
        });

        // ...composed with a scheduler-style pooled async tick loop.
        bool? tickOnPoolThread = null;
        var tickStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tickLoop = new DelegateBackgroundService(async cancellationToken =>
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            tickOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            tickStarted.SetResult();

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

        var options = new TestHostOptions();
        options.HostedServices.Add(flushWorker);
        options.HostedServices.Add(tickLoop);

        IHost host = new TestHost(options);

        // Act
        await host.StartAsync();
        await Task.WhenAll(flushStarted.Task, tickStarted.Task);
        await host.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Assert
        flushOnPoolThread.ShouldBe(false);
        tickOnPoolThread.ShouldBe(true);
        flushDrained.ShouldBeTrue();
    }
}
