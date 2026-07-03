using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

[Collection(nameof(SerialCollection))]
public class HostToServiceWrapperTests
{
    public const string DisplayPrefix = "Cohesion Test [Hosting] - HostToServiceWrapper: ";

    private static (TestHost Host, Task Started) CreateIdleHost()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TestHostOptions();

        options.HostedServices.Add(new DelegateBackgroundService(async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));

        return (new TestHost(options), started.Task);
    }

    [Fact(DisplayName = DisplayPrefix + "The stopped signal is pending until the host transitions to Stopped")]
    public async Task WhenStoppedAsync_BeforeStop_CompletesOnStoppedTransition()
    {
        // Arrange
        var context = new TestHostContext(new List<IHostService>());
        var stopped = context.WhenStoppedAsync();

        // Act & Assert
        stopped.IsCompleted.ShouldBeFalse();

        context.SetState(HostState.Starting);
        context.SetState(HostState.Started);
        stopped.IsCompleted.ShouldBeFalse();

        context.SetState(HostState.Stopping);
        stopped.IsCompleted.ShouldBeFalse();

        context.SetState(HostState.Stopped);
        await stopped.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    [Fact(DisplayName = DisplayPrefix + "The stopped signal is already completed for a stopped host")]
    public void WhenStoppedAsync_WhenAlreadyStopped_ReturnsCompletedTask()
    {
        // Arrange
        var context = new TestHostContext(new List<IHostService>());
        context.SetState(HostState.Stopped);

        // Act & Assert
        context.WhenStoppedAsync().IsCompleted.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "The stopped signal resets for the next run after a restart")]
    public async Task WhenStoppedAsync_AfterRestart_ProducesAFreshSignal()
    {
        // Arrange
        var context = new TestHostContext(new List<IHostService>());
        context.SetState(HostState.Stopped);
        context.SetState(HostState.Starting);
        context.SetState(HostState.Started);

        // Act
        var stopped = context.WhenStoppedAsync();

        // Assert
        stopped.IsCompleted.ShouldBeFalse();

        context.SetState(HostState.Stopped);
        await stopped.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    [Fact(DisplayName = DisplayPrefix + "The stopped signal also completes when the host fails")]
    public async Task WhenStoppedAsync_OnFailedTransition_Completes()
    {
        // Arrange
        var context = new TestHostContext(new List<IHostService>());
        var stopped = context.WhenStoppedAsync();

        // Act
        context.SetState(HostState.Starting);
        context.SetState(HostState.Failed);

        // Assert
        await stopped.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    [Fact(DisplayName = DisplayPrefix + "A nested host runs until the outer stop and is stopped by it")]
    public async Task StopAsync_AfterNestedHostStarted_StopsTheNestedHost()
    {
        // Arrange
        var (inner, started) = CreateIdleHost();
        var service = inner.AsService();

        // Act
        await service.StartAsync();
        await started;

        // Assert
        inner.Context.State.ShouldBe(HostState.Started);

        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        inner.Context.State.ShouldBe(HostState.Stopped);
    }

    [Fact(DisplayName = DisplayPrefix + "When the wrapped host stops on its own the wrapper completes without an outer cancel")]
    public async Task ExecuteAsync_WhenNestedHostStopsOnItsOwn_WrapperCompletesWithoutOuterCancel()
    {
        // Arrange
        var (inner, started) = CreateIdleHost();
        var service = inner.AsService();

        await service.StartAsync();
        await started;

        // Act - stop the wrapped host directly, not through the wrapper.
        await ((IHost)inner).StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await Task.Delay(500);

        // Assert - a pre-cancelled stop token only succeeds when the wrapper's work already
        // completed (a completed task wins over a cancelled token in WaitAsync); if the
        // wrapper were still parked, this would throw OperationCanceledException.
        await Should.NotThrowAsync(() => service.StopAsync(new CancellationToken(canceled: true)));
    }

    [Fact(DisplayName = DisplayPrefix + "An idle nested host does not busy-spin a core")]
    public async Task ExecuteAsync_WhileNestedHostIdles_DoesNotBusySpin()
    {
        // Arrange
        var (inner, started) = CreateIdleHost();
        var service = inner.AsService();

        await service.StartAsync();
        await started;

        // Let transient work (JIT, GC, expiring timers from sibling tests) settle.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(100);

        // Act - sample process CPU over idle windows. A busy-spin burns every window on a
        // full core, so a single quiet window proves the wrapper is parked, while process
        // noise (finalizers, timer callbacks from earlier tests) may legitimately blow the
        // budget in some windows.
        var quietWindowObserved = false;

        for (var attempt = 0; attempt < 4 && !quietWindowObserved; attempt++)
        {
            var before = Process.GetCurrentProcess().TotalProcessorTime;
            await Task.Delay(500);
            var consumed = Process.GetCurrentProcess().TotalProcessorTime - before;

            quietWindowObserved = consumed < TimeSpan.FromMilliseconds(250);
        }

        // Assert
        quietWindowObserved.ShouldBeTrue();

        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }
}
