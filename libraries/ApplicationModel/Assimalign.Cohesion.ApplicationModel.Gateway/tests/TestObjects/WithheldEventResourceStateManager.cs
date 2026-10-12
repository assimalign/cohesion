using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

internal sealed class WithheldEventResourceStateManager : IApplicationResourceStateManager
{
    private readonly List<ResourceStateChangedEventArgs> _withheld = new();

    public WithheldEventResourceStateManager()
    {
        Inner.StateChanged += (_, observation) =>
        {
            if (Withhold?.Invoke(observation) == true)
            {
                _withheld.Add(observation);
            }
            else
            {
                StateChanged?.Invoke(this, observation);
            }
        };
    }

    public InMemoryResourceStateManager Inner { get; } = new();

    public Func<ResourceStateChangedEventArgs, bool>? Withhold { get; set; }

    public int WaitCalls { get; private set; }

    public event EventHandler<ResourceStateChangedEventArgs>? StateChanged;

    public void DeliverWithheldEvents()
    {
        foreach (ResourceStateChangedEventArgs observation in _withheld)
        {
            StateChanged?.Invoke(this, observation);
        }

        _withheld.Clear();
    }

    public ResourceLifecycle GetState(ResourceId id) => Inner.GetState(id);

    public void SetState(ResourceId id, ResourceLifecycle state, string? detail = null,
        IReadOnlyList<ResourceEndpoint>? observedEndpoints = null) =>
        Inner.SetState(id, state, detail, observedEndpoints);

    public IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id) => Inner.GetObservedEndpoints(id);

    public Task<ResourceLifecycle> WaitForStateAsync(ResourceId id, IReadOnlySet<ResourceLifecycle> terminals,
        TimeSpan budget, CancellationToken cancellationToken = default)
    {
        WaitCalls++;
        return Inner.WaitForStateAsync(id, terminals, budget, cancellationToken);
    }

    public IReadOnlyList<ResourceCommandObservation> GetCommandObservations(ResourceId id) => Inner.GetCommandObservations(id);

    public void SetCommandObservation(ResourceId id, ResourceCommandObservation observation) => Inner.SetCommandObservation(id, observation);

    public void RemoveCommandObservation(ResourceId id, string owner, string commandId) => Inner.RemoveCommandObservation(id, owner, commandId);
}
