using System;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// The public state contract exposes details through events. Keep that contract intact,
// with a locked snapshot for the built-in store whose waiters may finish before its event.
internal sealed class ResourceStateDetailTracker : IDisposable
{
    private readonly IApplicationResourceStateManager _state;
    private readonly ConcurrentDictionary<ResourceId, ResourceStateChangedEventArgs> _observations = new();

    public ResourceStateDetailTracker(IApplicationResourceStateManager state)
    {
        _state = state;
        _state.StateChanged += OnStateChanged;
    }

    public string? GetDetail(ResourceId id) => TryReadDetail(_state, id, out string? detail)
        ? detail
        : _observations.TryGetValue(id, out ResourceStateChangedEventArgs? observation)
            ? observation.Detail
            : null;

    internal static bool TryReadDetail(
        IApplicationResourceStateManager state,
        ResourceId id,
        out string? detail)
    {
        if (state is InMemoryResourceStateManager memory)
        {
            detail = memory.GetDetail(id);
            return true;
        }

        if (state is ApplicationScopedResourceStateManager scoped)
        {
            return scoped.TryGetDetail(id, out detail);
        }

        detail = null;
        return false;
    }

    public void Dispose() => _state.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, ResourceStateChangedEventArgs args) =>
        _observations[args.Resource] = args;
}
