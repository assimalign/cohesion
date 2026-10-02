using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.Hosting.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class MultiplexedExchangeTrackerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Exchange tracker: The drain completes only after every started exchange has finished")]
    public async Task WhenDrainedAsync_WithExchangesInFlight_CompletesAfterTheLastOneFinishes()
    {
        // Arrange
        MultiplexedExchangeTracker tracker = new();
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Start(() => releaseFirst.Task);
        tracker.Start(() => releaseSecond.Task);

        // Act
        Task drained = tracker.WhenDrainedAsync();

        // Assert
        await Task.Delay(50);
        drained.IsCompleted.ShouldBeFalse();

        releaseFirst.TrySetResult();
        await Task.Delay(50);
        drained.IsCompleted.ShouldBeFalse();

        releaseSecond.TrySetResult();
        await Should.NotThrowAsync(() => drained.WaitAsync(_timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Exchange tracker: The drain completes at once when no exchange was started, and repeated calls share it")]
    public async Task WhenDrainedAsync_WithNoExchanges_CompletesImmediatelyAndIsIdempotent()
    {
        // Arrange
        MultiplexedExchangeTracker tracker = new();

        // Act
        Task first = tracker.WhenDrainedAsync();
        Task second = tracker.WhenDrainedAsync();

        // Assert
        await Should.NotThrowAsync(() => first.WaitAsync(_timeout));
        second.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Exchange tracker: A faulting exchange neither faults the drain nor escapes unobserved")]
    public async Task WhenDrainedAsync_WithFaultingExchange_CompletesSuccessfully()
    {
        // Arrange — one exchange throws synchronously, one faults asynchronously.
        MultiplexedExchangeTracker tracker = new();
        tracker.Start(() => throw new InvalidOperationException("synchronous defect"));
        tracker.Start(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("asynchronous defect");
        });

        // Act
        Task drained = tracker.WhenDrainedAsync();

        // Assert
        await Should.NotThrowAsync(() => drained.WaitAsync(_timeout));
        drained.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Exchange tracker: A synchronously blocking exchange does not hold back the caller")]
    public async Task Start_WithSynchronouslyBlockingExchange_ReturnsWithoutRunningItInline()
    {
        // Arrange — the exchange blocks its thread before its first await, the way a CPU-bound
        // middleware would; Start must not run it on the caller's (the receive loop's) thread.
        MultiplexedExchangeTracker tracker = new();
        using ManualResetEventSlim release = new();

        // Act
        Task startCall = Task.Run(() => tracker.Start(() =>
        {
            release.Wait(TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        }));

        // Assert — an inline run would hold Start for the full block.
        await Should.NotThrowAsync(() => startCall.WaitAsync(TimeSpan.FromSeconds(2)));

        release.Set();
        await Should.NotThrowAsync(() => tracker.WhenDrainedAsync().WaitAsync(_timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Web Hosting] - Exchange tracker: Start rejects a null exchange")]
    public void Start_WithNullExchange_Throws()
    {
        MultiplexedExchangeTracker tracker = new();

        Should.Throw<ArgumentNullException>(() => tracker.Start(null!));
    }
}
