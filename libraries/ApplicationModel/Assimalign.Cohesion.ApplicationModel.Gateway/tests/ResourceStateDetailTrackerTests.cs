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
    [Theory(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Readiness observation: later updates cannot replace the terminal detail")]
    [InlineData(ResourceLifecycle.Starting, "later starting")]
    [InlineData(ResourceLifecycle.Failed, "later failure")]
    [InlineData(ResourceLifecycle.Failed, null)]
    public async Task WaitForObservationAsync_FailureThenAnotherUpdate_RetainsCompletingObservation(ResourceLifecycle laterState, string? laterDetail)
    {
        // Arrange
        var state = new ForwardingResourceStateManager();
        using var tracker = new ResourceStateDetailTracker(state);
        ResourceId id = Guid.NewGuid();
        state.SetState(id, ResourceLifecycle.Starting, "old starting");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<ResourceStateObservation> wait = tracker.WaitForObservationAsync(id,
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

    [Fact(DisplayName = "Cohesion Test [ApplicationModel.Gateway] - Readiness observation: a failure without detail never inherits Starting detail")]
    public async Task WaitForObservationAsync_FailureWithoutDetail_ReturnsNoDetail()
    {
        // Arrange
        var state = new ForwardingResourceStateManager();
        using var tracker = new ResourceStateDetailTracker(state);
        ResourceId id = Guid.NewGuid();
        state.SetState(id, ResourceLifecycle.Starting, "old starting");
        state.SetState(id, ResourceLifecycle.Failed);

        // Act
        ResourceStateObservation observation = await tracker.WaitForObservationAsync(id,
            new HashSet<ResourceLifecycle> { ResourceLifecycle.Failed }, TimeSpan.FromSeconds(2), CancellationToken.None);

        // Assert
        observation.ShouldBe(new ResourceStateObservation(ResourceLifecycle.Failed, null));
    }
}
