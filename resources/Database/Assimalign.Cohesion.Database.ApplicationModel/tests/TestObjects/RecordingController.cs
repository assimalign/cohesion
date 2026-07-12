using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

/// <summary>
/// A controller that records the order in which resources are reconciled and deleted,
/// and drives each resource's observed state to <see cref="ResourceLifecycle.Running"/>
/// by default, or to <see cref="ResourceLifecycle.Failed"/> for names in <c>failing</c>.
/// It also injects an observed endpoint on success so the gateway records it.
/// </summary>
internal sealed class RecordingController : IApplicationResourceController
{
    private readonly List<string> _reconciled;
    private readonly List<string> _deleted;
    private readonly ISet<string> _failing;

    public RecordingController(List<string> reconciled, List<string> deleted, ISet<string>? failing = null)
    {
        _reconciled = reconciled;
        _deleted = deleted;
        _failing = failing ?? new HashSet<string>();
    }

    public bool CanControl(IApplicationResource resource) => true;

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        string name = context.Resource.Name.ToString();
        _reconciled.Add(name);

        if (_failing.Contains(name))
        {
            context.State.SetState(context.Resource.Id, ResourceLifecycle.Failed, "recording controller forced failure");
            return Task.CompletedTask;
        }

        // On success, publish an observed endpoint for endpoint resources so the state
        // manager records it (the platform-allocated port becomes concrete here).
        IReadOnlyList<ResourceEndpoint>? observed = null;
        if (context.Resource is IEndpointResource endpointResource && endpointResource.Endpoints.Count > 0)
        {
            ResourceEndpoint declared = endpointResource.Endpoints[0];
            observed = new[] { declared with { Port = 61000, Host = "127.0.0.1" } };
        }

        context.State.SetState(context.Resource.Id, ResourceLifecycle.Running, detail: null, observedEndpoints: observed);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        _deleted.Add(context.Resource.Name.ToString());
        return Task.CompletedTask;
    }
}
