using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// Observe lifecycle and detail together through the public event contract. A foreign
// manager's lifecycle-only waiter cannot identify which detail belongs to its result.
internal sealed class ResourceStateDetailTracker : IDisposable
{
    private readonly IApplicationResourceStateManager _state;
    private readonly InMemoryResourceStateManager _observations = new();

    public ResourceStateDetailTracker(IApplicationResourceStateManager state)
    {
        _state = state;
        _state.StateChanged += OnStateChanged;
    }

    public ResourceStateObservation GetObservation(ResourceId id) => _observations.GetObservation(id);

    public Task<ResourceStateObservation> WaitForObservationAsync(
        ResourceId id,
        IReadOnlySet<ResourceLifecycle> terminals,
        TimeSpan budget,
        CancellationToken cancellationToken = default) =>
        _observations.WaitForObservationAsync(id, terminals, budget, cancellationToken);

    public void Dispose() => _state.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, ResourceStateChangedEventArgs args) =>
        _observations.SetState(args.Resource, args.Current, args.Detail);
}
