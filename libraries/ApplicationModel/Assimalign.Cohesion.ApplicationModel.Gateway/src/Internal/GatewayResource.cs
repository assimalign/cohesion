using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// One resource of the run: its aggregate, its previous record entry, the plan its step made, where
// it stands in its startup window, and when each registered instance last renewed its lease.
//
// Until its step plans it, the resource runs as the previous run left it: the aggregate holds the
// recorded state and outputs, and the recorded instances are carried with fresh leases. The plan
// keeps what still applies. A plan of None for the same definition keeps everything. Any other plan
// drops the outputs and the carried instances that have not renewed or published since, because the
// platform replaces the workload, and a new definition also drops the state. A planned resource is
// current: the platform runs, or may already run, the definition of this run.
//
// A plan of None opens the gate at once, because the platform already runs the resource. Otherwise
// the resource is pending until an instance registers after the plan, or closed when its window
// closes first. An instance that registers before the plan runs the previous definition, so it is
// registered without opening the gate. Both transitions leave pending atomically, so a registration
// that races the deadline either wins or is refused. Planning, registration, renewal, and expiry
// share one lock, so an instance that renews or publishes again while its lease expires is either
// kept or dropped whole, never half.
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
    private readonly HashSet<ResourceInstanceKey> _carried = [];
    private InfrastructureChange _change;
    private string? _revision;
    private int _state;
    private int _current;
    private int _tornDown;

    public GatewayResource(ApplicationResource aggregate, ApplicationResourceManifest declared, GatewayResourceRecord? previous)
    {
        Aggregate = aggregate;
        Declared = declared;
        Previous = previous;

        foreach (ResourceManifest instance in previous?.Instances ?? [])
        {
            Aggregate.Register(instance);
            _renewed[instance.Key] = Stopwatch.GetTimestamp();
            _carried.Add(instance.Key);
        }
    }

    public ApplicationResource Aggregate { get; }

    // The resource as declared for the provider's platform: the model's manifest with only that
    // platform's extensions. The provider sees this, and the definition fingerprints it.
    public ApplicationResourceManifest Declared { get; }

    // The resource's entry in the record the run started from, or null when it had none.
    public GatewayResourceRecord? Previous { get; }

    // What the plan changes; meaningful once the resource is current.
    public InfrastructureChange Change => _change;

    // The revision the plan runs, or null until the resource is planned.
    public string? Revision => Volatile.Read(ref _revision);

    public Task Registered => _registered.Task;

    public bool IsClosed => Volatile.Read(ref _state) == closed;

    // Whether the resource has been planned, so the platform runs, or may already run, the definition
    // of this run. Until it does, the record keeps the resource's previous entry.
    public bool IsCurrent => Volatile.Read(ref _current) != 0;

    public bool IsTornDown => Volatile.Read(ref _tornDown) != 0;

    // Applies the plan the resource's step made. Call it before the provider deploys the resource,
    // because an in-process provider's instances publish before the call returns.
    public void Plan(InfrastructureChange change, string revision)
    {
        bool sameDefinition = Previous is not null && Previous.Revision == revision;
        lock (_leases)
        {
            if (!sameDefinition)
            {
                Aggregate.ClearState();
            }

            if (!sameDefinition || change != InfrastructureChange.None)
            {
                // The platform replaces the workload, so what the record held for it is gone; an
                // instance that renewed or published since still runs and stays registered.
                Aggregate.SetOutputs(FrozenDictionary<OutputName, string>.Empty);
                foreach (ResourceInstanceKey key in _carried)
                {
                    _renewed.Remove(key);
                    Aggregate.Unregister(key);
                }
            }

            _carried.Clear();
            _change = change;
            Volatile.Write(ref _revision, revision);
            Volatile.Write(ref _current, 1);
        }

        if (change == InfrastructureChange.None)
        {
            Volatile.Write(ref _state, registered);
            _registered.TrySetResult();
        }
    }

    public bool TryMarkRegistered()
    {
        if (!IsCurrent)
        {
            return !IsClosed;
        }

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

    // Registers an instance that published and starts its lease.
    public IReadOnlyList<ResourceStateSnapshot> Register(ResourceManifest instance)
    {
        lock (_leases)
        {
            IReadOnlyList<ResourceStateSnapshot> restore = Aggregate.Register(instance);
            _renewed[instance.Key] = Stopwatch.GetTimestamp();
            _carried.Remove(instance.Key);
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
            _carried.Remove(key);
            return true;
        }
    }

    public void Unregister(ResourceInstanceKey key)
    {
        lock (_leases)
        {
            _renewed.Remove(key);
            _carried.Remove(key);
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
                _carried.Remove(key);
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

        public string? Revision => _resource.Revision;

        public bool IsCurrent => _resource.IsCurrent;

        public bool IsClosed => _resource.IsClosed;
    }
}
