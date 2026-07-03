using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;


public class HostTests
{
    public const string DisplayPrefix = $"Cohesion Test [Hosting] - Host: ";

    [Fact(DisplayName = DisplayPrefix + "Ensure Lifecycle Service Start & Stop Order")]
    public async Task TestLifecycleServiceOrder()
    {
        var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var queue = new Queue<TestLifecycleService.Lifecycle>();

        var hostOptions = new TestHostOptions();

        hostOptions.HostedServices.Add(new TestLifecycleService(queue.Enqueue));

        var host = new TestHost(hostOptions);

        await host.RunAsync(cancellationTokenSource.Token);

        Assert.Equal(6, queue.Count);

        // Ensure lifecycle order
        Assert.Equal(TestLifecycleService.Lifecycle.Starting, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Start, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Started, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Stopping, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Stop, queue.Dequeue());
        Assert.Equal(TestLifecycleService.Lifecycle.Stopped, queue.Dequeue());
    }

    [Fact(DisplayName = DisplayPrefix + "All four host hooks fire in order around the service lifecycle")]
    public async Task Host_StartStopCycle_FiresAllFourHooksInOrder()
    {
        // Arrange
        var events = new List<string>();
        var options = new TestHostOptions();

        options.HostedServices.Add(new TestLifecycleService(lifecycle => events.Add(lifecycle.ToString())));

        IHost host = new HookRecordingHost(options, events.Add);

        // Act
        await host.StartAsync();
        await host.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Assert
        events.ShouldBe(new[]
        {
            "OnStarting", "Starting", "Start", "Started", "OnStarted",
            "OnStopping", "Stopping", "Stop", "Stopped", "OnStopped",
        });
    }

    [Fact(DisplayName = DisplayPrefix + "A cleanly stopped host can be started and stopped again")]
    public async Task Host_AfterCleanStop_CanBeRestarted()
    {
        // Arrange
        var events = new List<TestLifecycleService.Lifecycle>();
        var options = new TestHostOptions();

        options.HostedServices.Add(new TestLifecycleService(events.Add));

        IHost host = new TestHost(options);

        // Act - two full start/stop cycles on the same host instance.
        await host.StartAsync();
        host.Context.State.ShouldBe(HostState.Started);
        await host.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        host.Context.State.ShouldBe(HostState.Stopped);

        await host.StartAsync();
        host.Context.State.ShouldBe(HostState.Started);
        await host.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        host.Context.State.ShouldBe(HostState.Stopped);

        // Assert - the full lifecycle ran twice.
        var cycle = new[]
        {
            TestLifecycleService.Lifecycle.Starting,
            TestLifecycleService.Lifecycle.Start,
            TestLifecycleService.Lifecycle.Started,
            TestLifecycleService.Lifecycle.Stopping,
            TestLifecycleService.Lifecycle.Stop,
            TestLifecycleService.Lifecycle.Stopped,
        };

        events.ShouldBe(new List<TestLifecycleService.Lifecycle>([.. cycle, .. cycle]));
    }

    [Fact(DisplayName = DisplayPrefix + "Shutdown during RunAsync drains services with a fresh stop budget")]
    public async Task RunAsync_ShutdownRequested_DrainsServicesWithFreshBudget()
    {
        // Arrange
        var drained = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateBackgroundService(async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            // The drain step must be reachable: the stop budget cannot be a token that was
            // already cancelled by the shutdown signal itself.
            await Task.Delay(100, CancellationToken.None);
            drained = true;
        }));

        var host = new TestHost(options);

        // Act
        var run = host.RunAsync();
        await started.Task;
        host.Context.Shutdown();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        drained.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A direct StopAsync unparks a waiting RunAsync")]
    public async Task StopAsync_WhileRunAsyncParked_UnparksRunAsync()
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateBackgroundService(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));

        var host = new TestHost(options);
        var run = host.RunAsync();
        await started.Task;

        // Act - stop directly rather than signaling shutdown.
        await ((IHost)host).StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Assert
        await Should.NotThrowAsync(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "RunAsync can run the same host again after a completed run")]
    public async Task RunAsync_AfterPreviousRun_RunsAgain()
    {
        // Arrange
        var runs = 0;
        using var startedSignal = new SemaphoreSlim(0);
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateBackgroundService(async cancellationToken =>
        {
            Interlocked.Increment(ref runs);
            startedSignal.Release();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));

        var host = new TestHost(options);

        // Act - first run.
        var run1 = host.RunAsync();
        await startedSignal.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        host.Context.Shutdown();
        await run1.WaitAsync(TimeSpan.FromSeconds(10));

        // Second run of the same instance.
        var run2 = host.RunAsync();
        await startedSignal.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        host.Context.Shutdown();
        await run2.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        runs.ShouldBe(2);
    }
}