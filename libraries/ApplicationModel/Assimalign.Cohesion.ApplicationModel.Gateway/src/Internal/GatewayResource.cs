using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// One resource of the run: its aggregate, what its plan changed, the revision it runs, and where it
// stands in the startup window. A resource the plan left unchanged starts registered: the platform
// already runs it. Otherwise it is pending until its first instance registers, or closed when the
// window closes first. Both transitions leave pending atomically, so a registration that races the
// deadline either wins or is refused.
[DebuggerDisplay("{Aggregate.Manifest.Provider}/{Aggregate.Manifest.Name} {Change}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class GatewayResource
{
    private const int pending = 0;
    private const int registered = 1;
    private const int closed = 2;

    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;
    private int _tornDown;

    public GatewayResource(ApplicationResource aggregate, InfrastructureChange change, string revision)
    {
        Aggregate = aggregate;
        Change = change;
        Revision = revision;

        if (change == InfrastructureChange.None)
        {
            _state = registered;
            _registered.TrySetResult();
        }
    }

    public ApplicationResource Aggregate { get; }

    public InfrastructureChange Change { get; }

    public string Revision { get; }

    public Task Registered => _registered.Task;

    public bool IsClosed => Volatile.Read(ref _state) == closed;

    public bool IsTornDown => Volatile.Read(ref _tornDown) != 0;

    public bool TryMarkRegistered()
    {
        if (Interlocked.CompareExchange(ref _state, registered, pending) == closed)
        {
            return false;
        }

        _registered.TrySetResult();
        return true;
    }

    public bool TryClose()
    {
        return Interlocked.CompareExchange(ref _state, closed, pending) == pending;
    }

    public void MarkTornDown()
    {
        Volatile.Write(ref _tornDown, 1);
    }

    private sealed class DebugView
    {
        private readonly GatewayResource _resource;

        public DebugView(GatewayResource resource)
        {
            _resource = resource;
        }

        public ApplicationResource Aggregate => _resource.Aggregate;

        public InfrastructureChange Change => _resource.Change;

        public string Revision => _resource.Revision;

        public bool IsClosed => _resource.IsClosed;
    }
}
