using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class InMemoryResourceStateManagerTests
{
    private static readonly IReadOnlySet<ResourceLifecycle> InitialReadinessTerminals =
        new HashSet<ResourceLifecycle>
        {
            ResourceLifecycle.Running,
            ResourceLifecycle.Failed,
            ResourceLifecycle.Stopped,
        };

    private static ResourceId NewId() => Guid.NewGuid();

    [Fact]
    public async Task WaitForStateAsync_AlreadyTerminal_ReturnsImmediately()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        manager.SetState(id, ResourceLifecycle.Running);

        ResourceLifecycle reached = await manager.WaitForStateAsync(id, InitialReadinessTerminals, TimeSpan.FromSeconds(1));

        reached.ShouldBe(ResourceLifecycle.Running);
    }

    [Fact]
    public async Task WaitForStateAsync_SetAfterSubscribe_Completes()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();

        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(id, InitialReadinessTerminals, TimeSpan.FromSeconds(2));
        manager.SetState(id, ResourceLifecycle.Running);

        (await wait).ShouldBe(ResourceLifecycle.Running);
    }

    [Fact]
    public async Task WaitForStateAsync_Failure_ReturnsFailed()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();

        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(id, InitialReadinessTerminals, TimeSpan.FromSeconds(2));
        manager.SetState(id, ResourceLifecycle.Failed);

        (await wait).ShouldBe(ResourceLifecycle.Failed);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - WaitForStateAsync: Should return Stopped reached before Running")]
    public async Task WaitForStateAsync_WhenStateBecomesStopped_ShouldReturnStopped()
    {
        // Arrange
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(
            id,
            InitialReadinessTerminals,
            Timeout.InfiniteTimeSpan);

        // Act
        manager.SetState(id, ResourceLifecycle.Stopped);

        // Assert
        (await wait.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Stopped);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - WaitForStateAsync: Should keep Degraded non-gating until initial readiness")]
    public async Task WaitForStateAsync_WhenStateIsDegraded_ShouldContinueUntilInitialReadiness()
    {
        // Arrange
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(
            id,
            InitialReadinessTerminals,
            Timeout.InfiniteTimeSpan);

        // Act
        manager.SetState(id, ResourceLifecycle.Degraded);

        // Assert
        wait.IsCompleted.ShouldBeFalse();

        manager.SetState(id, ResourceLifecycle.Running);
        (await wait.WaitAsync(TimeSpan.FromSeconds(2))).ShouldBe(ResourceLifecycle.Running);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - WaitForStateAsync: Should let a claimed terminal state win a cancellation race")]
    public async Task WaitForStateAsync_WhenTerminalStateClaimsWaitBeforeCancellation_ShouldReturnTerminalState()
    {
        // Arrange
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        using var setStateEntered = new ManualResetEventSlim();
        using var releaseSetState = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var terminals = new TrackingTerminalSet(ResourceLifecycle.Running)
        {
            OnContains = state =>
            {
                if (state == ResourceLifecycle.Running)
                {
                    setStateEntered.Set();
                    if (!releaseSetState.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("The test did not release the state transition.");
                    }
                }
            },
        };
        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(
            id,
            terminals,
            Timeout.InfiniteTimeSpan,
            cancellation.Token);
        Task stateChange = Task.Run(() => manager.SetState(id, ResourceLifecycle.Running));

        // Act
        setStateEntered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        Task cancellationTask = cancellation.CancelAsync();
        releaseSetState.Set();

        // Assert
        await stateChange.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellationTask.WaitAsync(TimeSpan.FromSeconds(5));
        (await wait.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(ResourceLifecycle.Running);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - WaitForStateAsync: Should cancel and remove the waiter")]
    public async Task WaitForStateAsync_WhenCancellationIsRequested_ShouldThrowAndRemoveWaiter()
    {
        // Arrange
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        var terminals = new TrackingTerminalSet(
            ResourceLifecycle.Running,
            ResourceLifecycle.Failed,
            ResourceLifecycle.Stopped);
        using var cancellation = new CancellationTokenSource();
        Task<ResourceLifecycle> wait = manager.WaitForStateAsync(
            id,
            terminals,
            Timeout.InfiniteTimeSpan,
            cancellation.Token);

        // Act
        cancellation.Cancel();

        // Assert
        OperationCanceledException exception = await Should.ThrowAsync<OperationCanceledException>(
            async () => await wait.WaitAsync(TimeSpan.FromSeconds(2)));
        exception.CancellationToken.ShouldBe(cancellation.Token);

        terminals.ThrowOnContains = true;
        manager.SetState(id, ResourceLifecycle.Running);
        ResourceLifecycle reached = await manager.WaitForStateAsync(
            id,
            InitialReadinessTerminals,
            TimeSpan.FromSeconds(1));
        reached.ShouldBe(ResourceLifecycle.Running);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - WaitForStateAsync: Should return the last state and remove a timed-out waiter")]
    public async Task WaitForStateAsync_WhenBudgetExpires_ShouldReturnLastObservedAndRemoveWaiter()
    {
        // Arrange
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        var terminals = new TrackingTerminalSet(ResourceLifecycle.Running);
        manager.SetState(id, ResourceLifecycle.Starting);

        // Act
        ResourceLifecycle reached = await manager.WaitForStateAsync(
            id,
            terminals,
            TimeSpan.FromMilliseconds(50));

        // Assert
        reached.ShouldBe(ResourceLifecycle.Starting);

        terminals.ThrowOnContains = true;
        manager.SetState(id, ResourceLifecycle.Running);
        manager.GetState(id).ShouldBe(ResourceLifecycle.Running);
    }

    [Fact]
    public void SetState_WithObservedEndpoints_ExposesThem()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();

        manager.SetState(
            id,
            ResourceLifecycle.Running,
            observedEndpoints: new[] { new ResourceEndpoint("http", "http", 8080, Host: "localhost") });

        ResourceEndpoint endpoint = manager.GetObservedEndpoints(id).ShouldHaveSingleItem();
        endpoint.Port.ShouldBe(8080);
        endpoint.Host.ShouldBe("localhost");
    }

    [Fact]
    public void SetState_OnTransition_RaisesStateChanged()
    {
        var manager = new InMemoryResourceStateManager();
        ResourceId id = NewId();
        ResourceStateChangedEventArgs? captured = null;
        manager.StateChanged += (_, args) => captured = args;

        manager.SetState(id, ResourceLifecycle.Running);

        captured.ShouldNotBeNull();
        captured!.Previous.ShouldBe(ResourceLifecycle.Unknown);
        captured.Current.ShouldBe(ResourceLifecycle.Running);
    }
}
