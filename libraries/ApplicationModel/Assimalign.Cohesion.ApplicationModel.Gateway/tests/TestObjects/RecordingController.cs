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
    private readonly List<string> _stopped;
    private readonly ISet<string> _failing;
    private readonly bool _leaveStarting;
    private readonly IReadOnlyDictionary<string, ResourceLifecycle> _observedStates;
    private readonly TaskCompletionSource? _reconciledSignal;

    public RecordingController(
        List<string> reconciled,
        List<string> deleted,
        ISet<string>? failing = null,
        bool leaveStarting = false,
        IReadOnlyDictionary<string, ResourceLifecycle>? observedStates = null,
        List<string>? stopped = null,
        TaskCompletionSource? reconciledSignal = null)
    {
        _reconciled = reconciled;
        _deleted = deleted;
        _stopped = stopped ?? new List<string>();
        _failing = failing ?? new HashSet<string>();
        _leaveStarting = leaveStarting;
        _observedStates = observedStates ?? new Dictionary<string, ResourceLifecycle>();
        _reconciledSignal = reconciledSignal;
    }

    public bool CanRealize(ResourcePlan plan, out string? reason)
    {
        reason = null;
        return true;
    }

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
        _reconciledSignal?.TrySetResult();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        _deleted.Add(context.Resource.Name.ToString());
        return Task.CompletedTask;
    }

    public Task StopAsync(IResourceControlContext context, CancellationToken cancellationToken = default)
    {
        _stopped.Add(context.Resource.Name.ToString());
        return Task.CompletedTask;
    }
}
