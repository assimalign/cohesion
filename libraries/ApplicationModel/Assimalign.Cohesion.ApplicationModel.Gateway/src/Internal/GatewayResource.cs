using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// One resource of the run: its aggregate, what its plan changed, the revision it runs, where it
// stands in the startup window, and when each registered instance last renewed its lease.
//
// A resource the plan left unchanged starts registered: the platform already runs it. Otherwise it
// is pending until its first instance registers, or closed when the window closes first. Both
// transitions leave pending atomically, so a registration that races the deadline either wins or
// is refused. Registration, renewal, and expiry share one lock, so an instance that renews or
// publishes again while its lease expires is either kept or dropped whole, never half.
[DebuggerDisplay("{Aggregate.Manifest.Provider}/{Aggregate.Manifest.Name} {Change}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class GatewayResource
{
    private const int pending = 0;
    private const int registered = 1;
    private const int closed = 2;

    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _leases = new();
    private readonly Dictionary<ResourceInstanceKey, long> _renewed = new();
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

    // Registers an instance and starts its lease.
    public IReadOnlyList<ResourceStateSnapshot> Register(ResourceManifest instance)
    {
        lock (_leases)
        {
            IReadOnlyList<ResourceStateSnapshot> restore = Aggregate.Register(instance);
            _renewed[instance.Key] = Stopwatch.GetTimestamp();
            return restore;
        }
    }

    public bool TryRenew(ResourceInstanceKey key)
    {
        lock (_leases)
        {
            if (!_renewed.ContainsKey(key))
            {
                return false;
            }

            _renewed[key] = Stopwatch.GetTimestamp();
            return true;
        }
    }

    public void Unregister(ResourceInstanceKey key)
    {
        lock (_leases)
        {
            _renewed.Remove(key);
            Aggregate.Unregister(key);
        }
    }

    // Starts a fresh lease for every registered instance.
    public void RenewAll()
    {
        lock (_leases)
        {
            long now = Stopwatch.GetTimestamp();
            foreach (ResourceInstanceKey key in new List<ResourceInstanceKey>(_renewed.Keys))
            {
                _renewed[key] = now;
            }
        }
    }

    // Unregisters every instance whose lease lapsed, and returns how many.
    public int Expire(TimeSpan lease)
    {
        lock (_leases)
        {
            long now = Stopwatch.GetTimestamp();
            List<ResourceInstanceKey>? expired = null;
            foreach (KeyValuePair<ResourceInstanceKey, long> entry in _renewed)
            {
                if (Stopwatch.GetElapsedTime(entry.Value, now) > lease)
                {
                    (expired ??= []).Add(entry.Key);
                }
            }

            if (expired is null)
            {
                return 0;
            }

            foreach (ResourceInstanceKey key in expired)
            {
                _renewed.Remove(key);
                Aggregate.Unregister(key);
            }

            return expired.Count;
        }
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
