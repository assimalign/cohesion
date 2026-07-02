using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

public class DedicatedThreadServiceTests
{
    public const string DisplayPrefix = "Cohesion Test [Hosting] - DedicatedThreadService: ";

    [Fact(DisplayName = DisplayPrefix + "Run executes on a dedicated background thread, not the pool")]
    public async Task StartAsync_WhenStarted_RunsWorkOnDedicatedBackgroundThread()
    {
        // Arrange
        Thread? workThread = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DelegateDedicatedThreadService(cancellationToken =>
        {
            workThread = Thread.CurrentThread;
            started.SetResult();
            cancellationToken.WaitHandle.WaitOne();
        });

        // Act
        await service.StartAsync();
        await started.Task;

        // Assert
        var thread = workThread.ShouldNotBeNull();
        thread.IsThreadPoolThread.ShouldBeFalse();
        thread.IsBackground.ShouldBeTrue();
        thread.Name.ShouldBe(nameof(DelegateDedicatedThreadService));

        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    [Fact(DisplayName = DisplayPrefix + "Stop joins the thread and waits for the drain to finish")]
    public async Task StopAsync_AfterCancellationSignal_WaitsForRunToDrain()
    {
        // Arrange
        var drained = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DelegateDedicatedThreadService(cancellationToken =>
        {
            started.SetResult();
            cancellationToken.WaitHandle.WaitOne();

            // Simulate a drain step (e.g. a final flush) that runs after the stop signal.
            Thread.Sleep(100);
            drained = true;
        });

        // Act
        await service.StartAsync();
        await started.Task;
        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Assert
        drained.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A fault thrown by Run surfaces on stop instead of killing the process")]
    public async Task StopAsync_WhenRunThrows_SurfacesException()
    {
        // Arrange
        var service = new DelegateDedicatedThreadService(cancellationToken =>
            throw new InvalidOperationException("run fault"));

        // Act
        await service.StartAsync();
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        // Assert
        exception.Message.ShouldBe("run fault");
    }

    [Fact(DisplayName = DisplayPrefix + "A cooperative cancellation exit is a clean stop")]
    public async Task StopAsync_WhenRunExitsViaOperationCanceled_CompletesCleanly()
    {
        // Arrange
        var service = new DelegateDedicatedThreadService(cancellationToken =>
        {
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
        });

        // Act & Assert
        await service.StartAsync();
        await Should.NotThrowAsync(
            () => service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
    }

    [Fact(DisplayName = DisplayPrefix + "Work that ignores cancellation faults the stop when the budget expires, and a retried stop rejoins it")]
    public async Task StopAsync_WhenRunIgnoresCancellation_ThrowsWhenBudgetExpiresAndCanBeRetried()
    {
        // Arrange
        using var release = new ManualResetEventSlim(false);
        var service = new DelegateDedicatedThreadService(cancellationToken =>
        {
            // Ignore the stop signal entirely; only the test releases this work.
            release.Wait();
        });

        // Act
        await service.StartAsync();
        await Should.ThrowAsync<OperationCanceledException>(
            () => service.StopAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(200)).Token));

        release.Set();

        // Assert
        await Should.NotThrowAsync(
            () => service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
    }

    [Fact(DisplayName = DisplayPrefix + "The service can be started again after a clean stop")]
    public async Task StartAsync_AfterStop_RunsAgain()
    {
        // Arrange
        var runs = 0;
        var service = new DelegateDedicatedThreadService(cancellationToken =>
        {
            Interlocked.Increment(ref runs);
            cancellationToken.WaitHandle.WaitOne();
        });

        // Act
        await service.StartAsync();
        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await service.StartAsync();
        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Assert
        runs.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "Stop before start is a no-op")]
    public async Task StopAsync_BeforeStart_IsNoOp()
    {
        // Arrange
        var service = new DelegateDedicatedThreadService(cancellationToken => { });

        // Act & Assert
        await Should.NotThrowAsync(() => service.StopAsync());
    }
}
