using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// <see cref="RunAsync"/> loads the gateway's record from its <see cref="IGatewayStore"/> and deploys
/// in dependency order: a resource or child gateway deploys once everything it depends on is ready,
/// and independent ones deploy at the same time. At that point the gateway fills in the resource's
/// settings from the outputs of what it depends on and plans it with the infrastructure provider.
/// Only the resources whose plan changes something are deployed; the others keep running. A
/// resource is ready once an instance of its deployment has published and its outputs are read. A
/// resource keeps its recorded state while its definition (the model's and the provider's revision)
/// is unchanged, even when a value filled in from a dependency changes, and keeps its registered
/// instances while the platform keeps them running. Until it is planned, a resource runs as the
/// previous run left it. Every created or updated resource must have an instance publish within
/// <see cref="StartupTimeout"/> of its deployment: a new one that does not is torn down, an updated
/// one is left to the platform, what depends on it is not deployed, and the run fails with
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
    private FrozenDictionary<ApplicationResourceOutput, string> _received = FrozenDictionary<ApplicationResourceOutput, string>.Empty;
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
    /// Gets how long every created or updated resource has to publish its first manifest once its
    /// deployment starts.
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
    /// Gets the aggregates of the resources the gateway runs, in dependency order, each after the
    /// resources of this gateway it depends on; empty before <see cref="RunAsync"/>.
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
    /// <exception cref="ArgumentException"><paramref name="infrastructure"/> names no platform.</exception>
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
    /// <exception cref="ArgumentException"><paramref name="infrastructure"/> names no platform.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startupTimeout"/> or <paramref name="leaseDuration"/> is not positive.</exception>
    public static ApplicationGateway Create(IInfrastructureProvider infrastructure, IGatewayStore store, TimeSpan? startupTimeout = null, TimeSpan? leaseDuration = null)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentNullException.ThrowIfNull(store);

        if (infrastructure.Platform.Value is null)
        {
            throw new ArgumentException(ErrorMessages.GatewayPlatformMissing, nameof(infrastructure));
        }

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
    /// <exception cref="ArgumentException">
    /// The gateway, or a gateway below it, cannot deploy its part: a child gateway deploys as one step,
    /// so it cannot both wait for one of its parent's steps and be waited for by it; or a resource's
    /// setting uses an output its gateway cannot get, one of a resource neither in that gateway nor
    /// above it, or one from above this gateway that its deployment manifest does not carry. Nothing
    /// has been deployed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The gateway has already run, or the infrastructure provider returned no action list, no plan,
    /// or no outputs.
    /// </exception>
    /// <exception cref="ApplicationGatewayException">
    /// A created or updated resource published no manifest within <see cref="StartupTimeout"/> of its
    /// deployment (<see cref="ApplicationGatewayErrorCode.StartupTimedOut"/>), or a setting uses an
    /// output its resource's provider did not report (<see cref="ApplicationGatewayErrorCode.OutputMissing"/>).
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
            // Every gateway of this part must be able to deploy in order, and to fill in its
            // resources' settings, before anything deploys.
            GatewayStep[] steps = GatewayStep.Order(model.Deployment);
            ThrowIfUnordered(model.Deployment.Gateways);
            FrozenDictionary<ApplicationResourceOutput, string> received = Receive(model);
            ThrowIfUnresolvable(model, received);
            _received = received;

            GatewayRecord? record = await _store.LoadAsync(model.Name, cancellationToken).ConfigureAwait(false);
            CreateResources(model, record, steps);
            await DeployAsync(model, steps, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Gets the value recorded for one output of a resource this gateway runs.
    /// </summary>
    /// <param name="output">The placeholder for the output.</param>
    /// <param name="value">The recorded value, or <see langword="null"/> when there is none.</param>
    /// <returns>
    /// <see langword="true"/> when the gateway runs the resource and recorded the output;
    /// <see langword="false"/> when the resource belongs to another gateway, the gateway has not read
    /// its outputs yet, or the platform reported no such output.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    public bool TryGetOutput(ApplicationResourceOutput output, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(output);

        value = null;
        IApplicationModel? model = Model;
        FrozenDictionary<(ProviderName Provider, ResourceName Name), GatewayResource>? resources = Volatile.Read(ref _resources);
        if (model is null || resources is null || output.Gateway != model.Name)
        {
            return false;
        }

        return resources.TryGetValue((output.Provider, output.Name), out GatewayResource? resource)
            && resource.Aggregate.Outputs.TryGetValue(output.Output, out value);
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

    // Refuses a part with a child gateway, at any depth, that cannot deploy its own part in order.
    private static void ThrowIfUnordered(IReadOnlyList<ApplicationManifest> gateways)
    {
        foreach (ApplicationManifest gateway in gateways)
        {
            GatewayStep.Order(gateway);
            ThrowIfUnordered(gateway.Gateways);
        }
    }

    // The output values the gateway above passed down, by output.
    private static FrozenDictionary<ApplicationResourceOutput, string> Receive(IApplicationModel model)
    {
        var received = new Dictionary<ApplicationResourceOutput, string>();
        foreach (ApplicationResourceOutputValue value in model.Outputs)
        {
            received.Add(value.Output, value.Value);
        }

        return received.ToFrozenDictionary();
    }

    // Refuses a setting, in this gateway or any below it, whose output its gateway cannot get. A
    // gateway learns its own resources' outputs and receives the values its parent passes down, so an
    // output must belong to the setting's gateway or one above it; one from above this gateway must
    // be among the values this gateway received.
    private static void ThrowIfUnresolvable(IApplicationModel model, FrozenDictionary<ApplicationResourceOutput, string> received)
    {
        var parents = new Dictionary<GatewayName, GatewayName>();
        MapParents(model.Manifest, parents);
        var part = new HashSet<GatewayName>();
        AddNames(model.Deployment, part);
        ThrowIfUnresolvable(model, model.Deployment, parents, part, received);
    }

    private static void ThrowIfUnresolvable(
        IApplicationModel model,
        ApplicationManifest gateway,
        Dictionary<GatewayName, GatewayName> parents,
        HashSet<GatewayName> part,
        FrozenDictionary<ApplicationResourceOutput, string> received)
    {
        foreach (ApplicationResourceManifest resource in gateway.Resources)
        {
            foreach (ApplicationResourceSetting setting in resource.Settings)
            {
                if (setting.Output is not { } output)
                {
                    continue;
                }

                if (!IsSelfOrAbove(output.Gateway, gateway.Name, parents))
                {
                    throw new ArgumentException(
                        Format(ErrorMessages.GatewaySettingUnresolvable, setting.Name, resource.Provider, resource.Name, gateway.Name, output.Output, output.Provider, output.Name, output.Gateway),
                        "model");
                }

                if (!part.Contains(output.Gateway) && !received.ContainsKey(output))
                {
                    throw new ArgumentException(
                        Format(ErrorMessages.GatewaySettingOutputNotPassed, setting.Name, resource.Provider, resource.Name, gateway.Name, output.Output, output.Provider, output.Name, output.Gateway, model.Name),
                        "model");
                }
            }
        }

        foreach (ApplicationManifest child in gateway.Gateways)
        {
            ThrowIfUnresolvable(model, child, parents, part, received);
        }
    }

    private static void MapParents(ApplicationManifest gateway, Dictionary<GatewayName, GatewayName> parents)
    {
        foreach (ApplicationManifest child in gateway.Gateways)
        {
            parents.Add(child.Name, gateway.Name);
            MapParents(child, parents);
        }
    }

    private static void AddNames(ApplicationManifest gateway, HashSet<GatewayName> names)
    {
        names.Add(gateway.Name);
        foreach (ApplicationManifest child in gateway.Gateways)
        {
            AddNames(child, names);
        }
    }

    private static bool IsSelfOrAbove(GatewayName candidate, GatewayName gateway, Dictionary<GatewayName, GatewayName> parents)
    {
        GatewayName current = gateway;
        while (current != candidate)
        {
            if (!parents.TryGetValue(current, out current))
            {
                return false;
            }
        }

        return true;
    }

    private void CreateResources(IApplicationModel model, GatewayRecord? record, GatewayStep[] steps)
    {
        var recorded = new Dictionary<(ProviderName Provider, ResourceName Name), GatewayResourceRecord>();
        foreach (GatewayResourceRecord entry in record?.Resources ?? [])
        {
            recorded[(entry.Provider, entry.Name)] = entry;
        }

        var lookup = new Dictionary<(ProviderName Provider, ResourceName Name), GatewayResource>();
        foreach (ApplicationResourceManifest manifest in model.Deployment.Resources)
        {
            // The provider sees a resource as declared for its platform: no other platform's
            // extensions reach it.
            ApplicationResourceManifest declared = ForPlatform(manifest);
            IReadOnlyCollection<IApplicationResourceAction> actions = _infrastructure.GetResourceActions(model, declared)
                ?? throw new InvalidOperationException(Format(ErrorMessages.GatewayResourceActionsNull, manifest.Provider, manifest.Name));

            // Until its step plans it, a resource runs as the previous run left it: its recorded
            // state, outputs, and instances. The plan then keeps what still applies.
            recorded.Remove((manifest.Provider, manifest.Name), out GatewayResourceRecord? previous);
            ApplicationResource aggregate = ApplicationResource.Create(manifest, previous?.State, actions, previous?.Outputs);
            lookup.Add((manifest.Provider, manifest.Name), new GatewayResource(aggregate, declared, previous));
        }

        // The resources are kept in the order they deploy in.
        var ordered = new List<GatewayResource>();
        var aggregates = new List<IApplicationResource>();
        foreach (GatewayStep step in steps)
        {
            if (step.Resource is not null)
            {
                GatewayResource resource = lookup[(step.Resource.Provider, step.Resource.Name)];
                ordered.Add(resource);
                aggregates.Add(resource.Aggregate);
            }
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

    // Runs every step once the steps it waits for are ready, independent steps at the same time, then
    // fails the run when a resource missed its startup window.
    private async Task DeployAsync(IApplicationModel model, GatewayStep[] steps, CancellationToken cancellationToken)
    {
        using var walk = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new Task[steps.Length];
        for (int index = 0; index < steps.Length; index++)
        {
            running[index] = RunStepAsync(model, steps[index], walk);
        }

        // A step that fails cancels the others, so this reports the failure itself, not the
        // cancellations that followed it.
        await Task.WhenAll(running).ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);

        var missed = new List<string>();
        var skipped = new List<string>();
        foreach (GatewayStep step in steps)
        {
            if (step.IsSkipped)
            {
                skipped.Add(step.Name);
            }
            else if (step.Resource is not null && Find(step.Resource).IsClosed)
            {
                missed.Add(step.Name);
            }
        }

        if (missed.Count == 0)
        {
            return;
        }

        string message = skipped.Count == 0
            ? Format(ErrorMessages.GatewayStartupTimedOut, model.Name, missed.Count, StartupTimeout, string.Join(", ", missed))
            : Format(ErrorMessages.GatewayStartupTimedOutWithSkipped, model.Name, missed.Count, StartupTimeout, string.Join(", ", missed), string.Join(", ", skipped));

        throw new ApplicationGatewayException(ApplicationGatewayErrorCode.StartupTimedOut, message);
    }

    private async Task RunStepAsync(IApplicationModel model, GatewayStep step, CancellationTokenSource walk)
    {
        bool ready = false;
        try
        {
            foreach (GatewayStep dependency in step.Dependencies)
            {
                if (!await dependency.Completion.ConfigureAwait(false))
                {
                    // Something the step waits for did not start, so the step does not deploy.
                    step.Skip();
                    return;
                }
            }

            walk.Token.ThrowIfCancellationRequested();
            if (step.Resource is null)
            {
                await _infrastructure.DeployGatewayAsync(model, CreateDeployment(model, step.Gateway!), walk.Token).ConfigureAwait(false);
                await SaveAsync(walk.Token).ConfigureAwait(false);
                ready = true;
            }
            else
            {
                ready = await RunResourceAsync(model, Find(step.Resource), walk.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Any failure ends the walk: the steps still running stop, and the run reports it.
            walk.Cancel();
            throw;
        }
        finally
        {
            step.Complete(ready);
        }
    }

    // Fills in a resource's settings, plans it, deploys it when the plan changes it, waits for an
    // instance of the deployment to publish, and reads the resource's outputs; then what depends on
    // it can be planned.
    private async Task<bool> RunResourceAsync(IApplicationModel model, GatewayResource resource, CancellationToken cancellationToken)
    {
        ApplicationResourceManifest manifest = resource.Declared;
        IReadOnlyDictionary<SettingName, string> settings = ResolveSettings(manifest);
        InfrastructurePlan? plan = await _infrastructure.PlanResourceAsync(model, manifest, settings, cancellationToken).ConfigureAwait(false);
        if (plan?.Revision is null)
        {
            throw new InvalidOperationException(Format(ErrorMessages.GatewayResourcePlanNull, manifest.Provider, manifest.Name));
        }

        // The definition is the manifest as declared for this platform (settings as declared, and
        // only this platform's extensions) and the provider's revision. A value filled in from a
        // dependency is not part of it, so a dependency that moved does not cost this resource its
        // recorded state, and neither does an extension for another platform. Planned before the
        // call: an in-process provider's instances publish before it returns.
        resource.Plan(plan.Change, string.Concat(Fingerprint(manifest), ":", plan.Revision));
        if (plan.Change != InfrastructureChange.None)
        {
            await _infrastructure.DeployResourceAsync(model, manifest, settings, cancellationToken).ConfigureAwait(false);
            await SaveAsync(cancellationToken).ConfigureAwait(false);

            bool started;
            try
            {
                await resource.Registered.WaitAsync(StartupTimeout, cancellationToken).ConfigureAwait(false);
                started = true;
            }
            catch (TimeoutException)
            {
                // The window closed. A resource that registered in the meantime is not missed.
                started = !resource.TryClose();
            }

            if (!started)
            {
                // A new resource that never started is removed; an update's previous instances are
                // the platform's to keep or replace.
                if (resource.Change == InfrastructureChange.Create)
                {
                    await _infrastructure.TeardownResourceAsync(model, manifest, cancellationToken).ConfigureAwait(false);
                    resource.MarkTornDown();
                }

                await SaveAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        IReadOnlyDictionary<OutputName, string> outputs = await _infrastructure.GetResourceOutputsAsync(model, manifest, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(Format(ErrorMessages.GatewayResourceOutputsNull, manifest.Provider, manifest.Name));

        if (resource.Aggregate.SetOutputs(outputs))
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // The values of one of this gateway's resources' settings, by name.
    private IReadOnlyDictionary<SettingName, string> ResolveSettings(ApplicationResourceManifest manifest)
    {
        GatewayName gateway = Model!.Name;
        var settings = new Dictionary<SettingName, string>(manifest.Settings.Count);
        foreach (ApplicationResourceSetting setting in manifest.Settings)
        {
            settings.Add(setting.Name, setting.Output is { } output ? ResolveOutput(gateway, manifest, setting, output) : setting.Value!);
        }

        return settings.AsReadOnly();
    }

    // The value of an output a setting uses. One of this gateway's resources is ready by the time
    // anything that depends on it is planned or deployed, so its outputs have been read; any other is
    // from above, and the gateway above passed it down.
    private string ResolveOutput(GatewayName gateway, ApplicationResourceManifest resource, ApplicationResourceSetting setting, ApplicationResourceOutput output)
    {
        if (output.Gateway != Model!.Name)
        {
            return _received[output];
        }

        if (!Volatile.Read(ref _resources)![(output.Provider, output.Name)].Aggregate.Outputs.TryGetValue(output.Output, out string? value))
        {
            throw new ApplicationGatewayException(
                ApplicationGatewayErrorCode.OutputMissing,
                Format(ErrorMessages.GatewaySettingOutputMissing, setting.Name, resource.Provider, resource.Name, gateway, output.Output, output.Provider, output.Name));
        }

        return value;
    }

    // A child gateway's deployment manifest: the whole hierarchy, the child's name, and the values of
    // the outputs from this gateway and those above that settings in the child's part use. The child
    // fills in those settings, or passes the values further down; outputs of its own part are its own
    // to read. The provider adds where the child runs.
    private GatewayDeploymentManifest CreateDeployment(IApplicationModel model, ApplicationManifest child)
    {
        var part = new HashSet<GatewayName>();
        AddNames(child, part);
        var outputs = new Dictionary<ApplicationResourceOutput, ApplicationResourceOutputValue>();
        AddOutputs(child, part, outputs);
        return new GatewayDeploymentManifest(child.Name, model.Manifest, [.. outputs.Values], null);
    }

    private void AddOutputs(ApplicationManifest gateway, HashSet<GatewayName> part, Dictionary<ApplicationResourceOutput, ApplicationResourceOutputValue> outputs)
    {
        foreach (ApplicationResourceManifest resource in gateway.Resources)
        {
            foreach (ApplicationResourceSetting setting in resource.Settings)
            {
                if (setting.Output is { } output && !part.Contains(output.Gateway) && !outputs.ContainsKey(output))
                {
                    outputs.Add(output, new ApplicationResourceOutputValue(output, ResolveOutput(gateway.Name, resource, setting, output)));
                }
            }
        }

        foreach (ApplicationManifest child in gateway.Gateways)
        {
            AddOutputs(child, part, outputs);
        }
    }

    private GatewayResource Find(ApplicationResourceManifest manifest)
    {
        return Volatile.Read(ref _resources)![(manifest.Provider, manifest.Name)];
    }

    private async Task RetireAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        foreach (GatewayResourceRecord retired in Volatile.Read(ref _retiredResources))
        {
            await _infrastructure.TeardownResourceAsync(model, new ApplicationResourceManifest(retired.Provider, retired.Name, [], [], []), cancellationToken).ConfigureAwait(false);
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

            if (!resource.IsCurrent)
            {
                // Until the run plans a resource, its entry stays as the previous run left it, so a
                // run that fails or stops before reaching it leaves its record untouched.
                if (resource.Previous is not null)
                {
                    resources.Add(resource.Previous);
                }

                continue;
            }

            ApplicationResource aggregate = resource.Aggregate;
            resources.Add(new GatewayResourceRecord(
                aggregate.Manifest.Provider,
                aggregate.Manifest.Name,
                resource.Revision!,
                [.. aggregate.State],
                [.. aggregate.Instances],
                aggregate.Outputs));
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

    // The resource's definition in the model, as the JSON of its manifest as declared for this
    // platform: provider, name, dependencies, settings, and this platform's extensions.
    private static string Fingerprint(ApplicationResourceManifest manifest)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, GatewayJsonContext.Default.ApplicationResourceManifest);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    // The resource as declared for the provider's platform: the model's manifest without the
    // extensions of any other platform.
    private ApplicationResourceManifest ForPlatform(ApplicationResourceManifest manifest)
    {
        PlatformName platform = _infrastructure.Platform;
        var applicable = new List<ApplicationResourceExtension>(manifest.Extensions.Count);
        foreach (ApplicationResourceExtension extension in manifest.Extensions)
        {
            if (extension.Platform == platform)
            {
                applicable.Add(extension);
            }
        }

        return applicable.Count == manifest.Extensions.Count
            ? manifest
            : manifest with { Extensions = applicable.AsReadOnly() };
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
