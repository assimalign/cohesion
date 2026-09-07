using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A controller that records the order in which resources are reconciled and deleted, and drives
/// each resource's observed state — to <see cref="ResourceLifecycle.Running"/> by default, to
/// <see cref="ResourceLifecycle.Failed"/> for names in <c>failing</c>, or leaving it at
/// <see cref="ResourceLifecycle.Starting"/> when <c>leaveStarting</c> is set. Per-resource
/// states supplied through <c>observedStates</c> override the default and failure set.
/// </summary>
internal sealed class RecordingController : IApplicationResourceController
{
    private readonly List<string> _reconciled;
    private readonly List<string> _deleted;
    private readonly ISet<string> _failing;
    private readonly bool _leaveStarting;
    private readonly IReadOnlyDictionary<string, ResourceLifecycle> _observedStates;

    public RecordingController(
        List<string> reconciled,
        List<string> deleted,
        ISet<string>? failing = null,
        bool leaveStarting = false,
        IReadOnlyDictionary<string, ResourceLifecycle>? observedStates = null)
    {
        _reconciled = reconciled;
        _deleted = deleted;
        _failing = failing ?? new HashSet<string>();
        _leaveStarting = leaveStarting;
        _observedStates = observedStates ?? new Dictionary<string, ResourceLifecycle>();
    }

    public bool CanControl(IApplicationResource resource) => true;

    public Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        string name = context.Resource.Name.ToString();
        _reconciled.Add(name);

        ResourceLifecycle state;
        if (_leaveStarting)
        {
            state = ResourceLifecycle.Starting;
        }
        else if (!_observedStates.TryGetValue(name, out state))
        {
            state = _failing.Contains(name) ? ResourceLifecycle.Failed : ResourceLifecycle.Running;
        }

        context.State.SetState(context.Resource.Id, state);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        _deleted.Add(context.Resource.Name.ToString());
        return Task.CompletedTask;
    }
}
