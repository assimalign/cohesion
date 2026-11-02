using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

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
// the resource is pending until an instance of the plan's deployment registers, or closed when its
// window closes first. The deployment's id decides which instance counts: one that repeats another
// deployment's id was started by an earlier deployment, such as a pod of the previous rollout that
// restarted, and is registered without opening the gate. One that registered before the plan and
// repeats the id the plan computes runs exactly what the plan deploys, such as a pod restarted from
// the same definition while no gateway ran, but its registration predates the deployment, which may
// still remove it: Docker recreates the containers of a deployment caught in a restart loop. So it
// counts only when it renews after the deployment call returned (MarkDeployed), which shows it
// survived the deployment; one the deployment removed never renews, and its replacement's
// publication opens the gate instead. Every transition leaves pending atomically, so a registration
// or renewal that races the deadline either wins or leaves the window closed. Planning,
// registration, renewal, the deployment's mark, and expiry share one lock, so an instance that renews
// or publishes again while its lease expires is either kept or dropped whole, never half, and a
// renewal that races the mark is either before it, and does not count, or after it, and does.
//
// An operator's writes through the gateway run one at a time, from the call to the instance to the
// last restore, so every instance ends with the state the record holds.
[DebuggerDisplay("{Aggregate.Manifest.Provider}/{Aggregate.Manifest.Name} {Change}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class GatewayResource
{
    private const int pending = 0;
    private const int registered = 1;
    private const int closed = 2;

    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _leases = new();
    private readonly Dictionary<InstanceKey, long> _renewed = new();
    private readonly HashSet<InstanceKey> _carried = [];
    private ApplicationGatewayDeploymentChange _change;
    private string? _revision;
    private DeploymentId? _deployment;

    // Whether the provider's call that deploys the plan's deployment returned; read and written
    // under _leases.
    private bool _deployed;
    private Exception? _stopCause;
    private int _stopped;
    private int _state;
    private int _current;
    private int _tornDown;

    public GatewayResource(ApplicationGatewayDeployment aggregate, ApplicationResourceManifest declared, ApplicationGatewayDeploymentRecord? previous)
    {
        Aggregate = aggregate;
        Declared = declared;
        Previous = previous;

        foreach (ApplicationInstanceManifest instance in previous?.Instances ?? [])
        {
            Aggregate.Register(instance);
            _renewed[instance.Key] = Stopwatch.GetTimestamp();
            _carried.Add(instance.Key);
        }
    }

    public ApplicationGatewayDeployment Aggregate { get; }

    // Held by an operator's write from the call to the instance until every restore has ended.
    public SemaphoreSlim Writes { get; } = new(1, 1);

    // The resource as declared for the provider's platform: the model's manifest with only that
    // platform's extensions. The provider sees this, and the definition fingerprints it.
    public ApplicationResourceManifest Declared { get; }

    // The resource's entry in the record the run started from, or null when it had none.
    public ApplicationGatewayDeploymentRecord? Previous { get; }

    // What the plan changes; meaningful once the resource is current.
    public ApplicationGatewayDeploymentChange Change => _change;

    // The revision the plan runs, or null until the resource is planned.
    public string? Revision => Volatile.Read(ref _revision);

    // The deployment the plan makes, or null until the resource is planned and for a plan of None,
    // which deploys nothing. Written before the resource becomes current, and read only after.
    public DeploymentId? Deployment => _deployment;

    public Task Registered => _registered.Task;

    public bool IsClosed => Volatile.Read(ref _state) == closed;

    // Whether the resource has been planned, so the platform runs, or may already run, the definition
    // of this run. Until it does, the record keeps the resource's previous entry.
    public bool IsCurrent => Volatile.Read(ref _current) != 0;

    public bool IsTornDown => Volatile.Read(ref _tornDown) != 0;

    // Whether the deployment's workload stopped before an instance published, which closed the
    // window early; StopCause is the failure the provider reported, or null for a workload that ended.
    public bool IsStopped => Volatile.Read(ref _stopped) != 0;

    public Exception? StopCause => Volatile.Read(ref _stopCause);

    // Records why a workload stopped. Call it only once the window is closed.
    public void MarkStopped(Exception? cause)
    {
        Volatile.Write(ref _stopCause, cause);
        Volatile.Write(ref _stopped, 1);
    }

    // Applies the plan the resource's step made, with the deployment it makes unless the plan is None.
    // Call it before the provider deploys the resource, because an in-process provider's instances
    // publish before the call returns.
    //
    // The plan opens the gate only for a plan of None. An instance still registered that repeats the
    // plan's id registered before the deployment, which may yet remove it, so the plan does not count
    // it: it counts once it renews after MarkDeployed. The carried instances the plan drops are gone
    // before then, so the pod a rollout restart killed, still in a persistent store's record with the
    // same id, never counts.
    public void Plan(ApplicationGatewayDeploymentChange change, string revision, DeploymentId? deployment)
    {
        bool sameDefinition = Previous is not null && Previous.Revision == revision;
        lock (_leases)
        {
            if (!sameDefinition)
            {
                Aggregate.ClearState();
            }

            if (!sameDefinition || change != ApplicationGatewayDeploymentChange.None)
            {
                // The platform replaces the workload, so what the record held for it is gone; an
                // instance that renewed or published since still runs and stays registered.
                Aggregate.SetOutputs(FrozenDictionary<OutputName, string>.Empty);
                foreach (InstanceKey key in _carried)
                {
                    _renewed.Remove(key);
                    Aggregate.Unregister(key);
                }
            }

            _carried.Clear();
            _change = change;
            _deployment = deployment;
            Volatile.Write(ref _revision, revision);
            Volatile.Write(ref _current, 1);
        }

        if (change == ApplicationGatewayDeploymentChange.None)
        {
            Volatile.Write(ref _state, registered);
            _registered.TrySetResult();
        }
    }

    // Records that the provider's call deploying the plan's deployment returned. Call it right after
    // the call, before waiting for the deployment to start.
    //
    // Publications open before any step plans, so an instance can register first and only renew
    // afterwards; its registration saw the resource not yet current and left the gate shut. A
    // deployment's id is derived from what it deploys, so one that repeats the plan's id runs what the
    // plan deploys, but it registered before the deployment, which may have removed it: Docker
    // recreates the containers of a deployment caught in a restart loop, and a lease renewed before
    // the call shows only that the container ran then. From the mark on, a renewal of such an instance
    // opens the gate (TryRenew), because an instance the deployment removed never renews again. A
    // renewal before the mark, even one after the plan, proves nothing, since the call may still
    // remove the instance; it waits for the next one, at most a third of a lease later.
    public void MarkDeployed()
    {
        lock (_leases)
        {
            _deployed = true;
        }
    }

    // Opens the gate when the instance repeats the plan's deployment. Returns false only when the
    // window closed, so the registration is undone and refused.
    //
    // A publication after the plan counts even before the deployment's mark: an in-process provider's
    // instances publish before the call returns, and the instance published after the plan made the
    // deployment it repeats.
    public bool TryMarkRegistered(ApplicationInstanceManifest instance)
    {
        if (!IsCurrent || _deployment is { } deployment && instance.Deployment != deployment)
        {
            // An instance that registered before the plan, which counts once it renews after the
            // deployment's mark when it repeats the plan's deployment, or an instance of an earlier
            // deployment.
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
    public IReadOnlyList<ApplicationActionSnapshot> Register(ApplicationInstanceManifest instance)
    {
        lock (_leases)
        {
            IReadOnlyList<ApplicationActionSnapshot> restore = Aggregate.Register(instance);
            _renewed[instance.Key] = Stopwatch.GetTimestamp();
            _carried.Remove(instance.Key);
            return restore;
        }
    }

    // Renews a registered instance's lease. Once the deployment is marked, a renewal of an instance
    // whose registered manifest repeats the plan's deployment opens the gate, because the instance
    // survived the deployment (MarkDeployed); a window that closed stays closed. Returns false when
    // the instance is not registered, so it publishes again.
    public bool TryRenew(InstanceKey key)
    {
        bool survived = false;
        lock (_leases)
        {
            if (!_renewed.ContainsKey(key))
            {
                return false;
            }

            _renewed[key] = Stopwatch.GetTimestamp();
            _carried.Remove(key);
            if (_deployed && _deployment is { } deployment && Volatile.Read(ref _state) == pending)
            {
                foreach (ApplicationInstanceManifest instance in Aggregate.Instances)
                {
                    if (instance.Key == key)
                    {
                        survived = instance.Deployment == deployment;
                        break;
                    }
                }
            }
        }

        if (survived && Interlocked.CompareExchange(ref _state, registered, pending) == pending)
        {
            _registered.TrySetResult();
        }

        return true;
    }

    public void Unregister(InstanceKey key)
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
            foreach (InstanceKey key in new List<InstanceKey>(_renewed.Keys))
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
            List<InstanceKey>? expired = null;
            foreach (KeyValuePair<InstanceKey, long> entry in _renewed)
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

            foreach (InstanceKey key in expired)
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

        public ApplicationGatewayDeployment Aggregate => _resource.Aggregate;

        public ApplicationGatewayDeploymentChange Change => _resource.Change;

        public string? Revision => _resource.Revision;

        public DeploymentId? Deployment => _resource.Deployment;

        public bool IsCurrent => _resource.IsCurrent;

        public bool IsClosed => _resource.IsClosed;
    }
}
