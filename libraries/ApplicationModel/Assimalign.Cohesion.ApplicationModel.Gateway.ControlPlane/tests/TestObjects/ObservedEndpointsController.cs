using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Tests;

/// <summary>
/// Realizes every planned resource as Running with a fixed list of observed endpoints, so a test can
/// present the gateway with the endpoint shapes a platform might report.
/// </summary>
internal sealed class ObservedEndpointsController : IApplicationResourceController
{
    private readonly IReadOnlyList<ResourceEndpoint> _observed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservedEndpointsController"/> class.
    /// </summary>
    /// <param name="observed">The endpoints reported, in order, for every realized resource.</param>
    public ObservedEndpointsController(params ResourceEndpoint[] observed)
    {
        _observed = observed;
    }

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        reason = null;
        return true;
    }

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.State.SetState(context.Resource.Id, ResourceLifecycle.Running, observedEndpoints: _observed);
        return Task.CompletedTask;
    }

    public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        context.State.SetState(context.Resource.Id, ResourceLifecycle.Stopped);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
        StopAsync(context, cancellationToken);
}
