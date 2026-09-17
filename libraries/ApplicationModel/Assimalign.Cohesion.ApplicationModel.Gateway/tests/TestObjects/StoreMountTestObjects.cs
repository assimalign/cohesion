using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

internal sealed class StoreEndpointResolver(ResourceEndpoint endpoint) : IExternalResourceResolver
{
    public int CallCount { get; private set; }

    public ValueTask<ExternalResourceResolution> ResolveAsync(
        ExternalResourceResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return ValueTask.FromResult(new ExternalResourceResolution(true, [endpoint]));
    }
}

internal sealed class StoreMountController : IApplicationResourceController
{
    public ResourceInputs? Inputs { get; private set; }

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        reason = null;
        return true;
    }

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Inputs = context.Inputs;
        ResourceMountInput mount = context.Inputs.Mounts["cfg"];
        context.State.SetState(context.Resource.Id,
            mount.IsResolved ? ResourceLifecycle.Running : ResourceLifecycle.Failed,
            mount.UnresolvedReason);
        return Task.CompletedTask;
    }

    public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
