using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// One deployed resource's aggregate and where it stands in the startup window: pending until its
// first instance registers, then registered; or torn down when the window closes first. Both
// transitions leave pending atomically, so a registration that races the deadline either wins or
// is refused.
[DebuggerDisplay("{Aggregate.Manifest.Provider}/{Aggregate.Manifest.Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class GatewayResource
{
    private const int pending = 0;
    private const int registered = 1;
    private const int tornDown = 2;

    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    public GatewayResource(ApplicationResource aggregate)
    {
        Aggregate = aggregate;
    }

    public ApplicationResource Aggregate { get; }

    public Task Registered => _registered.Task;

    public bool IsTornDown => Volatile.Read(ref _state) == tornDown;

    public bool TryMarkRegistered()
    {
        if (Interlocked.CompareExchange(ref _state, registered, pending) == tornDown)
        {
            return false;
        }

        _registered.TrySetResult();
        return true;
    }

    public bool TryTearDown()
    {
        return Interlocked.CompareExchange(ref _state, tornDown, pending) == pending;
    }

    private sealed class DebugView
    {
        private readonly GatewayResource _resource;

        public DebugView(GatewayResource resource)
        {
            _resource = resource;
        }

        public ApplicationResource Aggregate => _resource.Aggregate;

        public bool IsTornDown => _resource.IsTornDown;
    }
}
