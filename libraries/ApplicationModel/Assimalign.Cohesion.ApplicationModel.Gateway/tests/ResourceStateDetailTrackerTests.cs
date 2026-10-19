using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class ResourceStateDetailTrackerTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Built-in readiness observation: later updates cannot replace the terminal detail")]
    [InlineData(ResourceLifecycle.Starting, "later starting")]
    [InlineData(ResourceLifecycle.Failed, "later failure")]
    [InlineData(ResourceLifecycle.Failed, null)]
    public async Task WaitForObservationAsync_FailureThenAnotherUpdate_RetainsCompletingObservation(ResourceLifecycle laterState, string? laterDetail)
    {
        // Arrange
        var state = new InMemoryResourceStateManager();
        using var tracker = new ResourceStateDetailTracker(state);
        ResourceId id = Guid.NewGuid();
        state.SetState(id, ResourceLifecycle.Starting, "old starting");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<ResourceStateObservation> wait = state.WaitForObservationAsync(id,
            new HashSet<ResourceLifecycle> { ResourceLifecycle.Failed }, TimeSpan.FromSeconds(2), cancellation.Token);

        // Act
        state.SetState(id, ResourceLifecycle.Failed, "terminal failure");
        state.SetState(id, laterState, laterDetail);
        ResourceStateObservation observation = await wait;

        // Assert
        observation.State.ShouldBe(ResourceLifecycle.Failed);
        observation.Detail.ShouldBe("terminal failure");
        tracker.GetObservation(id).ShouldBe(new ResourceStateObservation(laterState, laterDetail));
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Failure detail: a failure without detail never inherits Starting detail")]
    public async Task WaitForDetailAsync_FailureWithoutDetail_ReturnsNoDetail()
    {
        // Arrange
        var state = new ForwardingResourceStateManager();
        using var tracker = new ResourceStateDetailTracker(state);
        ResourceId id = Guid.NewGuid();
        state.SetState(id, ResourceLifecycle.Starting, "old starting");
        state.SetState(id, ResourceLifecycle.Failed);

        // Act
        string? detail = await tracker.WaitForDetailAsync(id,
            ResourceLifecycle.Failed, TimeSpan.FromSeconds(2), CancellationToken.None);

        // Assert
        detail.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Foreign failure detail: only the latest matching lifecycle supplies text")]
    [InlineData(ResourceLifecycle.Starting, null)]
    [InlineData(ResourceLifecycle.Failed, "Latest detail")]
    public async Task WaitForDetailAsync_LatestObservationChanged_RequiresMatchingLifecycle(ResourceLifecycle latest, string? expected)
    {
        // Arrange
        var state = new ForwardingResourceStateManager();
        using var tracker = new ResourceStateDetailTracker(state);
        ResourceId id = Guid.NewGuid();
        state.SetState(id, ResourceLifecycle.Failed, "Earlier failure");
        state.SetState(id, latest, "Latest detail");

        // Act
        string? detail = await tracker.WaitForDetailAsync(id, ResourceLifecycle.Failed, TimeSpan.Zero, CancellationToken.None);

        // Assert
        detail.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Failure detail: cancellation abandons pairing")]
    public async Task WaitForDetailAsync_Cancelled_AbandonsPairing()
    {
        // Arrange
        var state = new ForwardingResourceStateManager();
        using var tracker = new ResourceStateDetailTracker(state);
        ResourceId id = Guid.NewGuid();
        state.SetState(id, ResourceLifecycle.Starting, "Starting detail");
        using var cancellation = new CancellationTokenSource();
        Task<string?> wait = tracker.WaitForDetailAsync(id, ResourceLifecycle.Failed, TimeSpan.FromSeconds(10), cancellation.Token);

        // Act
        cancellation.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(() => wait);
        state.SetState(id, ResourceLifecycle.Failed, "Later detail");
        (await tracker.WaitForDetailAsync(id, ResourceLifecycle.Failed, TimeSpan.Zero, CancellationToken.None)).ShouldBe("Later detail");
    }
}
