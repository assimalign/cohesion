using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A controller that records the order in which resources are reconciled and deleted, and drives
/// each resource's observed state — to <see cref="ResourceLifecycle.Running"/> by default, to
/// <see cref="ResourceLifecycle.Failed"/> for names in <c>failing</c>, or leaving it at
/// <see cref="ResourceLifecycle.Starting"/> when <c>leaveStarting</c> is set.
/// </summary>
internal sealed class RecordingController : IApplicationResourceController
{
    private readonly List<string> _reconciled;
    private readonly List<string> _deleted;
    private readonly ISet<string> _failing;
    private readonly bool _leaveStarting;

    public RecordingController(
        List<string> reconciled,
        List<string> deleted,
        ISet<string>? failing = null,
        bool leaveStarting = false)
    {
        _reconciled = reconciled;
        _deleted = deleted;
        _failing = failing ?? new HashSet<string>();
        _leaveStarting = leaveStarting;
    }

    public bool CanControl(IApplicationResource resource) => true;

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        string name = context.Resource.Name.ToString();
        _reconciled.Add(name);

        ResourceLifecycle state = _leaveStarting
            ? ResourceLifecycle.Starting
            : _failing.Contains(name) ? ResourceLifecycle.Failed : ResourceLifecycle.Running;

        context.State.SetState(context.Resource.Id, state);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        _deleted.Add(context.Resource.Name.ToString());
        return Task.CompletedTask;
    }
}
