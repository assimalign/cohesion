using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

internal sealed class ForwardingResourceStateManager : IApplicationResourceStateManager
{
    public InMemoryResourceStateManager Inner { get; } = new();

    public event EventHandler<ResourceStateChangedEventArgs>? StateChanged
    {
        add => Inner.StateChanged += value;
        remove => Inner.StateChanged -= value;
    }

    public ResourceLifecycle GetState(ResourceId id) => Inner.GetState(id);

    public void SetState(ResourceId id, ResourceLifecycle state, string? detail = null,
        IReadOnlyList<ResourceEndpoint>? observedEndpoints = null) =>
        Inner.SetState(id, state, detail, observedEndpoints);

    public IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id) => Inner.GetObservedEndpoints(id);

    public Task<ResourceLifecycle> WaitForStateAsync(ResourceId id, IReadOnlySet<ResourceLifecycle> terminals,
        TimeSpan budget, CancellationToken cancellationToken = default) =>
        Inner.WaitForStateAsync(id, terminals, budget, cancellationToken);

    public IReadOnlyList<ResourceCommandObservation> GetCommandObservations(ResourceId id) => Inner.GetCommandObservations(id);

    public void SetCommandObservation(ResourceId id, ResourceCommandObservation observation) => Inner.SetCommandObservation(id, observation);

    public void RemoveCommandObservation(ResourceId id, string owner, string commandId) => Inner.RemoveCommandObservation(id, owner, commandId);
}
