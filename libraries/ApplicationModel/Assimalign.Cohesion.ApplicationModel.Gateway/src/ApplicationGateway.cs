using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// A gateway: it reconciles what an application's model declares with what its infrastructure runs,
/// receives the manifests its resources' instances publish, and keeps each resource's state until
/// the resource is next redefined.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunAsync"/> loads the gateway's record from its <see cref="IGatewayStore"/>, plans each
/// resource with the infrastructure provider, deploys only the resources whose plan changes
/// something, and leaves the others running. A resource keeps its recorded state while its
/// definition (the model's and the provider's revision) is unchanged, and keeps its registered
/// instances while the platform keeps them running. Every created or updated resource must have an
/// instance publish within <see cref="StartupTimeout"/>: a new one that does not is torn down, an
/// updated one is left to the platform, and the run fails with
/// <see cref="ApplicationGatewayErrorCode.StartupTimedOut"/>. After startup the gateway tears down the
/// resources and child gateways its record holds but the model no longer declares, then runs until
/// cancelled. Cancellation stops the gateway; it does not tear down what the gateway deployed.
/// </para>
/// <para>
/// The gateway saves its record after every change, so a gateway restarted with the same model
/// adopts what runs instead of deploying it again. Every registration carries a lease of
/// <see cref="LeaseDuration"/>: an instance renews it (<see cref="RenewAsync"/>), and one that stops
/// renewing is unregistered once it lapses. After startup every registered instance, the adopted
/// ones included, has one full lease to renew, so an instance that died while the gateway was down
/// leaves within one lease. The gateway is transport-neutral: a transport hands each publication to
/// <see cref="PublishAsync"/>, and an in-process resource can publish to the gateway directly,
/// because it is an <see cref="IResourceManifestPublisher"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("{Model}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ApplicationGateway : IApplicationGateway, IResourceManifestPublisher
{
    private const int idle = 0;
    private const int running = 1;
    private const int stopped = 2;

    private static readonly TimeSpan _defaultStartupTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _defaultLeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _minimumExpiryInterval = TimeSpan.FromMilliseconds(10);

    private readonly IInfrastructureProvider _infrastructure;
    private readonly IGatewayStore _store;
    private readonly SemaphoreSlim _saves = new(1, 1);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FrozenDictionary<(ProviderName Provider, ResourceName Name), GatewayResource>? _resources;
    private GatewayResource[] _ordered = [];
    private GatewayResourceRecord[] _retiredResources = [];
    private GatewayName[] _retiredGateways = [];
    private ReadOnlyCollection<IApplicationResource> _aggregates = ReadOnlyCollection<IApplicationResource>.Empty;
    private IApplicationModel? _model;
    private int _lifecycle;

    private ApplicationGateway(IInfrastructureProvider infrastructure, IGatewayStore store, TimeSpan startupTimeout, TimeSpan leaseDuration)
    {
        _infrastructure = infrastructure;
        _store = store;
        StartupTimeout = startupTimeout;
        LeaseDuration = leaseDuration;
    }

    /// <summary>
    /// Gets how long every created or updated resource has to publish its first manifest.
    /// </summary>
    public TimeSpan StartupTimeout { get; }

    /// <summary>
    /// Gets how long an instance stays registered without renewing its lease.
    /// </summary>
    public TimeSpan LeaseDuration { get; }

    /// <summary>
    /// Gets the model the gateway runs, or <see langword="null"/> before <see cref="RunAsync"/>.
    /// </summary>
    public IApplicationModel? Model => Volatile.Read(ref _model);

    /// <summary>
    /// Gets the aggregates of the resources the gateway runs, in deployment order; empty before
    /// <see cref="RunAsync"/>.
    /// </summary>
    public IReadOnlyCollection<IApplicationResource> Resources => Volatile.Read(ref _aggregates);

    /// <summary>
    /// Gets a task that completes when every created or updated resource has published and the
    /// resources the model no longer declares are torn down, faults when startup fails, and is
    /// canceled when the run is cancelled before startup completes.
    /// </summary>
    public Task Started => _started.Task;

    /// <summary>
    /// Creates a gateway that deploys through the specified infrastructure provider and keeps its
    /// record in memory, so a new gateway object starts without one.
    /// </summary>
    /// <param name="infrastructure">The infrastructure the gateway deploys onto.</param>
    /// <param name="startupTimeout">
    /// How long every created or updated resource has to publish its first manifest; five minutes
    /// when <see langword="null"/>.
    /// </param>
    /// <returns>The gateway.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="infrastructure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startupTimeout"/> is not positive.</exception>
    public static ApplicationGateway Create(IInfrastructureProvider infrastructure, TimeSpan? startupTimeout = null)
    {
        return Create(infrastructure, new InMemoryGatewayStore(), startupTimeout);
    }

    /// <summary>
    /// Creates a gateway that deploys through the specified infrastructure provider and keeps its
    /// record in a store, so a restarted gateway reconciles against it.
    /// </summary>
    /// <param name="infrastructure">The infrastructure the gateway deploys onto.</param>
    /// <param name="store">Where the gateway keeps its record.</param>
    /// <param name="startupTimeout">
    /// How long every created or updated resource has to publish its first manifest; five minutes
    /// when <see langword="null"/>.
    /// </param>
    /// <param name="leaseDuration">
    /// How long an instance stays registered without renewing; thirty seconds when <see langword="null"/>.
    /// </param>
    /// <returns>The gateway.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="infrastructure"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startupTimeout"/> or <paramref name="leaseDuration"/> is not positive.</exception>
    public static ApplicationGateway Create(IInfrastructureProvider infrastructure, IGatewayStore store, TimeSpan? startupTimeout = null, TimeSpan? leaseDuration = null)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentNullException.ThrowIfNull(store);

        TimeSpan timeout = startupTimeout ?? _defaultStartupTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(startupTimeout));

        TimeSpan lease = leaseDuration ?? _defaultLeaseDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero, nameof(leaseDuration));

        return new ApplicationGateway(infrastructure, store, timeout, lease);
    }

    /// <summary>
    /// Reconciles the model with the infrastructure and runs the gateway until cancellation.
    /// </summary>
    /// <param name="model">The model of what the gateway deploys.</param>
    /// <param name="cancellationToken">Signals that the gateway should stop.</param>
    /// <returns>A task that completes when the gateway stops.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The gateway has already run, or the infrastructure provider returned no action list or no plan.
    /// </exception>
    /// <exception cref="ApplicationGatewayException">
    /// A created or updated resource published no manifest within <see cref="StartupTimeout"/>.
    /// </exception>
    public async Task RunAsync(IApplicationModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Interlocked.CompareExchange(ref _lifecycle, running, idle) != idle)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationGatewayAlreadyRun);
        }

        try
        {
            GatewayRecord? record = await _store.LoadAsync(model.Name, cancellationToken).ConfigureAwait(false);
            await CreateResourcesAsync(model, record, cancellationToken).ConfigureAwait(false);

            foreach (ApplicationManifest gateway in model.Deployment.Gateways)
            {
                await _infrastructure.DeployGatewayAsync(model, gateway.Name, cancellationToken).ConfigureAwait(false);
            }

            foreach (GatewayResource resource in _ordered)
            {
                if (resource.Change != InfrastructureChange.None)
                {
                    await _infrastructure.DeployResourceAsync(model, resource.Aggregate.Manifest, cancellationToken).ConfigureAwait(false);
                }
            }

            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await AwaitStartupAsync(model, cancellationToken).ConfigureAwait(false);

            // Retire what the model no longer declares only once the deployment has started, so a
            // failed deployment leaves the previous one's resources running.
            await RetireAsync(model, cancellationToken).ConfigureAwait(false);

            // Every registered instance, the adopted ones included, has one full lease from here.
            foreach (GatewayResource resource in _ordered)
            {
                resource.RenewAll();
            }

            _started.TrySetResult();

            await ExpireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation stops the gateway; what it deployed keeps running.
            _started.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            // Every other failure ends the run; Started reports it before the caller sees it.
            _started.TrySetException(exception);
            throw;
        }
        finally
        {
            Volatile.Write(ref _lifecycle, stopped);
        }
    }

    /// <summary>
    /// Registers a published instance manifest with its resource, saves the gateway's record, and
    /// returns the state the instance must restore before it serves, with its lease.
    /// </summary>
    /// <param name="manifest">The manifest the instance published.</param>
    /// <param name="cancellationToken">Cancels saving the record.</param>
    /// <returns>The snapshots the instance must restore, and a lease of <see cref="LeaseDuration"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The manifest has no instance key or no action list.</exception>
    /// <exception cref="ApplicationGatewayException">
    /// The gateway is not running (<see cref="ApplicationGatewayErrorCode.PublicationsClosed"/>), does
    /// not run the resource (<see cref="ApplicationGatewayErrorCode.ResourceUnknown"/>), or stopped
    /// accepting it after it missed its startup window (<see cref="ApplicationGatewayErrorCode.ResourceTornDown"/>).
    /// </exception>
    public ValueTask<ResourcePublication> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        GatewayResource resource = GetResource(manifest.Provider, manifest.Name);
        if (resource.IsClosed)
        {
            throw Closed(resource);
        }

        IReadOnlyList<ResourceStateSnapshot> restore = resource.Register(manifest);
        if (!resource.TryMarkRegistered())
        {
            // The startup window closed between the check above and the registration.
            resource.Unregister(manifest.Key);
            throw Closed(resource);
        }

        return SaveRegistrationAsync(resource, manifest.Key, restore, cancellationToken);
    }

    /// <summary>
    /// Renews a registered instance's lease.
    /// </summary>
    /// <param name="manifest">The manifest the instance published.</param>
    /// <param name="cancellationToken">Unused; renewal completes synchronously.</param>
    /// <returns>
    /// <see langword="true"/> when the lease was renewed; <see langword="false"/> when the gateway
    /// does not know the instance, which then publishes again.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ApplicationGatewayException">
    /// The gateway is not running (<see cref="ApplicationGatewayErrorCode.PublicationsClosed"/>), does
    /// not run the resource (<see cref="ApplicationGatewayErrorCode.ResourceUnknown"/>), or stopped
    /// accepting it after it missed its startup window (<see cref="ApplicationGatewayErrorCode.ResourceTornDown"/>).
    /// </exception>
    public ValueTask<bool> RenewAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        GatewayResource resource = GetResource(manifest.Provider, manifest.Name);
        if (resource.IsClosed)
        {
            throw Closed(resource);
        }

        return ValueTask.FromResult(resource.TryRenew(manifest.Key));
    }

    /// <summary>
    /// Records the snapshot a stateful write left on one instance of a resource, saves the gateway's
    /// record, and returns the other instances that must restore the snapshot.
    /// </summary>
    /// <param name="provider">The resource's provider.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="source">The key of the instance the write ran on.</param>
    /// <param name="snapshot">The snapshot the write returned.</param>
    /// <param name="cancellationToken">Cancels saving the record.</param>
    /// <returns>The keys of the instances that must restore the snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="source"/> is not registered.</exception>
    /// <exception cref="ArgumentException">The source instance does not declare the snapshot's action stateful.</exception>
    /// <exception cref="ApplicationGatewayException">
    /// The gateway is not running (<see cref="ApplicationGatewayErrorCode.PublicationsClosed"/>) or does
    /// not run the resource (<see cref="ApplicationGatewayErrorCode.ResourceUnknown"/>).
    /// </exception>
    public ValueTask<IReadOnlyList<ResourceInstanceKey>> RecordAsync(
        ProviderName provider,
        ResourceName name,
        ResourceInstanceKey source,
        ResourceStateSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IReadOnlyList<ResourceInstanceKey> targets = GetResource(provider, name).Aggregate.Record(source, snapshot);
        return SaveRecordingAsync(targets, cancellationToken);
    }

    private async ValueTask<ResourcePublication> SaveRegistrationAsync(
        GatewayResource resource,
        ResourceInstanceKey key,
        IReadOnlyList<ResourceStateSnapshot> restore,
        CancellationToken cancellationToken)
    {
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // An instance whose registration was not recorded fails its start and publishes again.
            resource.Unregister(key);
            throw;
        }

        return new ResourcePublication(restore, LeaseDuration);
    }

    // Unregisters every instance whose lease lapsed, three times per lease, until the run is
    // cancelled.
    private async Task ExpireAsync(CancellationToken cancellationToken)
    {
        TimeSpan interval = LeaseDuration / 3;
        if (interval < _minimumExpiryInterval)
        {
            interval = _minimumExpiryInterval;
        }

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            int expired = 0;
            foreach (GatewayResource resource in _ordered)
            {
                expired += resource.Expire(LeaseDuration);
            }

            if (expired > 0)
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<IReadOnlyList<ResourceInstanceKey>> SaveRecordingAsync(IReadOnlyList<ResourceInstanceKey> targets, CancellationToken cancellationToken)
    {
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return targets;
    }

    private GatewayResource GetResource(ProviderName provider, ResourceName name)
    {
        FrozenDictionary<(ProviderName Provider, ResourceName Name), GatewayResource>? resources = Volatile.Read(ref _resources);
        if (resources is null || Volatile.Read(ref _lifecycle) != running)
        {
            throw new ApplicationGatewayException(ApplicationGatewayErrorCode.PublicationsClosed, ErrorMessages.GatewayPublicationsClosed);
        }

        if (!resources.TryGetValue((provider, name), out GatewayResource? resource))
        {
            throw new ApplicationGatewayException(
                ApplicationGatewayErrorCode.ResourceUnknown,
                Format(ErrorMessages.GatewayResourceUnknown, Model!.Name, provider, name));
        }

        return resource;
    }

    private async Task CreateResourcesAsync(IApplicationModel model, GatewayRecord? record, CancellationToken cancellationToken)
    {
        var recorded = new Dictionary<(ProviderName Provider, ResourceName Name), GatewayResourceRecord>();
        foreach (GatewayResourceRecord entry in record?.Resources ?? [])
        {
            recorded[(entry.Provider, entry.Name)] = entry;
        }

        var lookup = new Dictionary<(ProviderName Provider, ResourceName Name), GatewayResource>();
        var ordered = new List<GatewayResource>();
        var aggregates = new List<IApplicationResource>();
        foreach (ApplicationResourceManifest manifest in model.Deployment.Resources)
        {
            IReadOnlyCollection<IApplicationResourceAction> actions = _infrastructure.GetResourceActions(model, manifest)
                ?? throw new InvalidOperationException(Format(ErrorMessages.GatewayResourceActionsNull, manifest.Provider, manifest.Name));

            InfrastructurePlan? plan = await _infrastructure.PlanResourceAsync(model, manifest, cancellationToken).ConfigureAwait(false);
            if (plan?.Revision is null)
            {
                throw new InvalidOperationException(Format(ErrorMessages.GatewayResourcePlanNull, manifest.Provider, manifest.Name));
            }

            // The state recorded for a definition survives as long as the definition does; the
            // registered instances survive only while the platform keeps them running.
            string revision = string.Concat(Fingerprint(manifest), ":", plan.Revision);
            recorded.Remove((manifest.Provider, manifest.Name), out GatewayResourceRecord? previous);
            bool sameDefinition = previous is not null && previous.Revision == revision;

            ApplicationResource aggregate = ApplicationResource.Create(manifest, sameDefinition ? previous!.State : null, actions);
            var resource = new GatewayResource(aggregate, plan.Change, revision);
            if (sameDefinition && plan.Change == InfrastructureChange.None)
            {
                foreach (ResourceManifest instance in previous!.Instances)
                {
                    resource.Register(instance);
                }
            }

            lookup.Add((manifest.Provider, manifest.Name), resource);
            ordered.Add(resource);
            aggregates.Add(aggregate);
        }

        // What the record holds and the model no longer declares is retired after startup.
        var retiredResources = new List<GatewayResourceRecord>();
        foreach (GatewayResourceRecord entry in record?.Resources ?? [])
        {
            if (recorded.ContainsKey((entry.Provider, entry.Name)))
            {
                retiredResources.Add(entry);
            }
        }

        var declaredGateways = new HashSet<GatewayName>();
        foreach (ApplicationManifest gateway in model.Deployment.Gateways)
        {
            declaredGateways.Add(gateway.Name);
        }

        var retiredGateways = new List<GatewayName>();
        foreach (GatewayName gateway in record?.Gateways ?? [])
        {
            if (!declaredGateways.Contains(gateway))
            {
                retiredGateways.Add(gateway);
            }
        }

        _ordered = [.. ordered];
        Volatile.Write(ref _retiredResources, [.. retiredResources]);
        Volatile.Write(ref _retiredGateways, [.. retiredGateways]);
        Volatile.Write(ref _model, model);
        Volatile.Write(ref _aggregates, aggregates.AsReadOnly());

        // Publications are accepted from here, before any resource is deployed.
        Volatile.Write(ref _resources, lookup.ToFrozenDictionary());
    }

    private async Task AwaitStartupAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(StartupTimeout);

        var missed = new List<GatewayResource>();
        foreach (GatewayResource resource in _ordered)
        {
            try
            {
                await resource.Registered.WaitAsync(window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The window closed. A resource that registered in the meantime is not missed.
                if (resource.TryClose())
                {
                    missed.Add(resource);
                }
            }
        }

        if (missed.Count == 0)
        {
            return;
        }

        var names = new string[missed.Count];
        for (int index = 0; index < missed.Count; index++)
        {
            ApplicationResourceManifest manifest = missed[index].Aggregate.Manifest;
            if (missed[index].Change == InfrastructureChange.Create)
            {
                // A new resource that never started is removed; an update's previous instances are
                // the platform's to keep or replace.
                await _infrastructure.TeardownResourceAsync(model, manifest, cancellationToken).ConfigureAwait(false);
                missed[index].MarkTornDown();
            }

            names[index] = string.Concat(manifest.Provider.ToString(), "/", manifest.Name.ToString());
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);

        throw new ApplicationGatewayException(
            ApplicationGatewayErrorCode.StartupTimedOut,
            Format(ErrorMessages.GatewayStartupTimedOut, model.Name, missed.Count, StartupTimeout, string.Join(", ", names)));
    }

    private async Task RetireAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        foreach (GatewayResourceRecord retired in Volatile.Read(ref _retiredResources))
        {
            await _infrastructure.TeardownResourceAsync(model, new ApplicationResourceManifest(retired.Provider, retired.Name, []), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _retiredResources, Array.FindAll(Volatile.Read(ref _retiredResources), entry => !ReferenceEquals(entry, retired)));
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (GatewayName retired in Volatile.Read(ref _retiredGateways))
        {
            await _infrastructure.TeardownGatewayAsync(model, retired, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _retiredGateways, Array.FindAll(Volatile.Read(ref _retiredGateways), name => name != retired));
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Saves never overlap, and each writes the whole record as it stands when the save begins.
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saves.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(CreateRecord(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saves.Release();
        }
    }

    private GatewayRecord CreateRecord()
    {
        IApplicationModel model = Model!;

        var resources = new List<GatewayResourceRecord>();
        foreach (GatewayResource resource in _ordered)
        {
            if (resource.IsTornDown)
            {
                continue;
            }

            ApplicationResource aggregate = resource.Aggregate;
            resources.Add(new GatewayResourceRecord(
                aggregate.Manifest.Provider,
                aggregate.Manifest.Name,
                resource.Revision,
                [.. aggregate.State],
                [.. aggregate.Instances]));
        }

        // A retired resource or gateway stays recorded until its teardown succeeds, so a failed
        // run retries it.
        resources.AddRange(Volatile.Read(ref _retiredResources));

        var gateways = new List<GatewayName>();
        foreach (ApplicationManifest gateway in model.Deployment.Gateways)
        {
            gateways.Add(gateway.Name);
        }

        gateways.AddRange(Volatile.Read(ref _retiredGateways));

        return new GatewayRecord(model.Name, gateways.AsReadOnly(), resources.AsReadOnly());
    }

    // The resource's definition in the model, as its manifest's JSON: provider, name, dependencies.
    private static string Fingerprint(ApplicationResourceManifest manifest)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, GatewayJsonContext.Default.ApplicationResourceManifest);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    private ApplicationGatewayException Closed(GatewayResource resource)
    {
        ApplicationResourceManifest manifest = resource.Aggregate.Manifest;
        string format = resource.Change == InfrastructureChange.Create
            ? ErrorMessages.GatewayResourceTornDown
            : ErrorMessages.GatewayResourceUpdateMissed;

        return new ApplicationGatewayException(
            ApplicationGatewayErrorCode.ResourceTornDown,
            Format(format, Model!.Name, manifest.Provider, manifest.Name));
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ApplicationGateway _gateway;

        public DebugView(ApplicationGateway gateway)
        {
            _gateway = gateway;
        }

        public IApplicationModel? Model => _gateway.Model;

        public TimeSpan StartupTimeout => _gateway.StartupTimeout;

        public IReadOnlyCollection<IApplicationResource> Resources => _gateway.Resources;
    }
}
