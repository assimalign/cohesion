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

    [Fact(DisplayName = DisplayPrefix + "A failing service start rolls back started services and marks the host Failed")]
    public async Task StartAsync_WhenServiceFailsToStart_RollsBackStartedServicesAndFails()
    {
        // Arrange - a healthy service that starts first...
        var healthyStopped = false;
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateBackgroundService(async cancellationToken =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            healthyStopped = true;
        }));

        // ...followed by a service whose start fails.
        options.HostedServices.Add(new DelegateHostService(
            cancellationToken => throw new InvalidOperationException("start failure")));

        IHost host = new TestHost(options);

        // Act
        var exception = await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync());

        // Assert
        exception.Message.ShouldBe("start failure");
        host.Context.State.ShouldBe(HostState.Failed);
        healthyStopped.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A failed start does not wedge the host: stop and a retried start work")]
    public async Task StartAsync_AfterFailedStart_HostCanBeStoppedAndStartedAgain()
    {
        // Arrange - a service that fails its first start and succeeds afterwards.
        var attempts = 0;
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateHostService(cancellationToken =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException(new InvalidOperationException("transient start failure"))
                : Task.CompletedTask;
        }));

        IHost host = new TestHost(options);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync());
        host.Context.State.ShouldBe(HostState.Failed);

        // Assert - stopping the failed host is a clean no-op, and a retried start succeeds.
        await Should.NotThrowAsync(() => host.StopAsync());
        host.Context.State.ShouldBe(HostState.Failed);

        await host.StartAsync();
        host.Context.State.ShouldBe(HostState.Started);

        await host.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "A cancelled startup tears down started services and marks the host Failed")]
    public async Task StartAsync_CancelledDuringStartup_TearsDownAndFails()
    {
        // Arrange - a healthy first service...
        var healthyStopped = false;
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateBackgroundService(async cancellationToken =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            healthyStopped = true;
        }));

        // ...and a second whose start only finishes when the token is honored.
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        options.HostedServices.Add(new DelegateHostService(async cancellationToken =>
        {
            startEntered.SetResult();
            await blockStart.Task.WaitAsync(cancellationToken);
        }));

        IHost host = new TestHost(options);
        using var startTokenSource = new CancellationTokenSource();

        // Act - cancel while the second service is mid-start.
        var start = host.StartAsync(startTokenSource.Token);
        await startEntered.Task;
        startTokenSource.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => start);
        host.Context.State.ShouldBe(HostState.Failed);
        healthyStopped.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "StopAsync honors StopServicesConcurrently")]
    public async Task StopAsync_WithStopServicesConcurrently_StopsServicesConcurrently()
    {
        // Arrange - two services whose stops can only finish if both are in flight at once.
        var enteredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new TestHostOptions
        {
            StopServicesConcurrently = true,
        };

        options.HostedServices.Add(new DelegateHostService(
            start: cancellationToken => Task.CompletedTask,
            stop: async cancellationToken =>
            {
                enteredFirst.SetResult();
                await enteredSecond.Task.WaitAsync(cancellationToken);
            }));

        options.HostedServices.Add(new DelegateHostService(
            start: cancellationToken => Task.CompletedTask,
            stop: async cancellationToken =>
            {
                enteredSecond.SetResult();
                await enteredFirst.Task.WaitAsync(cancellationToken);
            }));

        IHost host = new TestHost(options);

        // Act & Assert - a serial stop would deadlock each service on the other and abort
        // on the stop budget; a concurrent stop completes.
        await host.StartAsync();
        await Should.NotThrowAsync(
            () => host.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "A failing service stop does not leak the services behind it")]
    public async Task StopAsync_WhenAServiceFailsToStop_StillStopsRemainingServices()
    {
        // Arrange - stop order is reverse registration: third, second (throws), first.
        var firstStopped = false;
        var thirdStopped = false;
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateHostService(
            start: cancellationToken => Task.CompletedTask,
            stop: cancellationToken =>
            {
                firstStopped = true;
                return Task.CompletedTask;
            }));

        options.HostedServices.Add(new DelegateHostService(
            start: cancellationToken => Task.CompletedTask,
            stop: cancellationToken => throw new InvalidOperationException("stop failure")));

        options.HostedServices.Add(new DelegateHostService(
            start: cancellationToken => Task.CompletedTask,
            stop: cancellationToken =>
            {
                thirdStopped = true;
                return Task.CompletedTask;
            }));

        IHost host = new TestHost(options);

        // Act
        await host.StartAsync();
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => host.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        // Assert - the failure surfaced, but every other service was still stopped.
        exception.Message.ShouldBe("stop failure");
        thirdStopped.ShouldBeTrue();
        firstStopped.ShouldBeTrue();
        host.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "Multiple stop failures aggregate with a stop message")]
    public async Task StopAsync_WhenMultipleServicesFailToStop_ThrowsAggregateWithStopMessage()
    {
        // Arrange
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateHostService(
            start: cancellationToken => Task.CompletedTask,
            stop: cancellationToken => throw new InvalidOperationException("first stop failure")));

        options.HostedServices.Add(new DelegateHostService(
            start: cancellationToken => Task.CompletedTask,
            stop: cancellationToken => throw new InvalidOperationException("second stop failure")));

        IHost host = new TestHost(options);

        // Act
        await host.StartAsync();
        var exception = await Should.ThrowAsync<AggregateException>(
            () => host.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        // Assert
        exception.Message.ShouldContain("failed to stop");
        exception.InnerExceptions.Count.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "Stopping or disposing a never-started host is a no-op")]
    public async Task StopAsync_NeverStartedHost_IsNoOpAndDisposeDoesNotThrow()
    {
        // Arrange
        IHost neverStartedForStop = new TestHost(new TestHostOptions());
        IHost neverStartedForAsyncDispose = new TestHost(new TestHostOptions());
        IHost neverStartedForSyncDispose = new TestHost(new TestHostOptions());

        // Act & Assert
        await Should.NotThrowAsync(() => neverStartedForStop.StopAsync());
        neverStartedForStop.Context.State.ShouldBe(HostState.Idle);

        await Should.NotThrowAsync(() => neverStartedForAsyncDispose.DisposeAsync().AsTask());
        Should.NotThrow(() => neverStartedForSyncDispose.Dispose());
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