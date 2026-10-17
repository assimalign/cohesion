using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// Events supply diagnostics for foreign managers only. Their delivery order must never
// decide readiness or override the built-in manager's atomic state/detail observations.
internal sealed class ResourceStateDetailTracker : IDisposable
{
    private readonly IApplicationResourceStateManager _state;
    private readonly InMemoryResourceStateManager _observations = new();

    public ResourceStateDetailTracker(IApplicationResourceStateManager state)
    {
        _state = state;
        _state.StateChanged += OnStateChanged;
    }

    public ResourceStateObservation GetObservation(ResourceId id) => _state switch
    {
        InMemoryResourceStateManager memory => memory.GetObservation(id),
        ApplicationScopedResourceStateManager scoped => scoped.GetObservation(id) ?? _observations.GetObservation(id),
        _ => _observations.GetObservation(id),
    };

    public async Task<string?> WaitForDetailAsync(
        ResourceId id,
        ResourceLifecycle reached,
        TimeSpan budget,
        CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        var matching = new HashSet<ResourceLifecycle> { reached };
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceStateObservation latest = _observations.GetObservation(id);
            if (latest.State == reached)
            {
                return latest.Detail;
            }

            TimeSpan remaining = budget - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await _observations.WaitForObservationAsync(id, matching, remaining, cancellationToken).ConfigureAwait(false);
            // A later event may have replaced the matching observation before continuation.
            // Recheck the latest pair; never return a different lifecycle's detail.
        }
    }

    public void Dispose() => _state.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, ResourceStateChangedEventArgs args) =>
        _observations.SetState(args.Resource, args.Current, args.Detail);
}
