using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

public class BackgroundServiceTests
{
    public const string DisplayPrefix = "Cohesion Test [Hosting] - BackgroundService: ";

    [Fact(DisplayName = DisplayPrefix + "Stop joins the real work task and waits for the drain to finish")]
    public async Task StopAsync_AfterCancellationSignal_WaitsForWorkToDrain()
    {
        // Arrange
        var drained = false;
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DelegateBackgroundService(async cancellationToken =>
        {
            running.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            // Simulate a drain step that runs after the stop signal.
            await Task.Delay(100, CancellationToken.None);
            drained = true;
        });

        // Act
        await service.StartAsync();
        await running.Task;
        await service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        // Assert
        drained.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "A fault thrown after the first await surfaces on stop")]
    public async Task StopAsync_WhenExecuteFaultsAfterYield_SurfacesException()
    {
        // Arrange
        var service = new DelegateBackgroundService(async cancellationToken =>
        {
            await Task.Yield();
            throw new InvalidOperationException("post-yield fault");
        });

        // Act
        await service.StartAsync();
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));

        // Assert
        exception.Message.ShouldBe("post-yield fault");
    }

    [Fact(DisplayName = DisplayPrefix + "A synchronous fault surfaces to the host during start")]
    public async Task StartAsync_WhenExecuteFaultsSynchronously_SurfacesException()
    {
        // Arrange
        var service = new DelegateBackgroundService(cancellationToken =>
            throw new InvalidOperationException("synchronous fault"));

        // Act & Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => service.StartAsync());

        exception.Message.ShouldBe("synchronous fault");
    }

    [Fact(DisplayName = DisplayPrefix + "A cooperative cancellation exit is a clean stop")]
    public async Task StopAsync_WhenWorkExitsViaCancellation_CompletesCleanly()
    {
        // Arrange
        var service = new DelegateBackgroundService(cancellationToken =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

        // Act & Assert
        await service.StartAsync();
        await Should.NotThrowAsync(
            () => service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
    }

    [Fact(DisplayName = DisplayPrefix + "Work that ignores cancellation faults the stop when the budget expires")]
    public async Task StopAsync_WhenWorkIgnoresCancellation_ThrowsWhenBudgetExpires()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DelegateBackgroundService(async cancellationToken =>
        {
            // Ignore the stop signal entirely; only the test releases this work.
            await release.Task;
        });

        // Act
        await service.StartAsync();
        var stop = Should.ThrowAsync<OperationCanceledException>(
            () => service.StopAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(200)).Token));

        // Assert
        await stop;
        release.SetResult();
    }

    [Fact(DisplayName = DisplayPrefix + "A timed-out stop can be retried and joins the same work")]
    public async Task StopAsync_AfterDrainTimeout_CanBeRetried()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DelegateBackgroundService(async cancellationToken =>
        {
            await release.Task;
        });

        // Act
        await service.StartAsync();
        await Should.ThrowAsync<OperationCanceledException>(
            () => service.StopAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(200)).Token));

        release.SetResult();

        // Assert
        await Should.NotThrowAsync(
            () => service.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token));
    }

    [Fact(DisplayName = DisplayPrefix + "Stop before start is a no-op")]
    public async Task StopAsync_BeforeStart_IsNoOp()
    {
        // Arrange
        var service = new DelegateBackgroundService(cancellationToken => Task.CompletedTask);

        // Act & Assert
        await Should.NotThrowAsync(() => service.StopAsync());
    }
}
