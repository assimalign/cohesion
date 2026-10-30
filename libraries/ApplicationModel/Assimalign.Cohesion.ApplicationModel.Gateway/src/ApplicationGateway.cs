using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// A gateway: it reconciles what its model declares with what its infrastructure runs, receives the
/// manifests its resources' instances publish, keeps each resource's state until the resource is next
/// redefined, and lets child gateways join it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunAsync"/> loads the gateway's record from its <see cref="IApplicationGatewayStore"/>.
/// A gateway whose model names a parent then joins it and waits, within <see cref="StartupTimeout"/>,
/// until the parent reports every output of its resources that the gateway's settings use. The
/// gateway then deploys in dependency order: a resource deploys once everything it depends on is
/// ready, and independent ones deploy at the same time. At that point the gateway fills in the
/// resource's settings from the outputs of what it depends on, or from its parent's answer, and plans
/// it with the infrastructure provider. Only the resources whose plan changes something are deployed;
/// the others keep running. A resource is ready once an instance of its deployment has published and
/// its outputs are read. A resource keeps its recorded state while its definition (the model's and
/// the provider's revision) is unchanged, even when a value filled in from a dependency changes, and
/// keeps its registered instances while the platform keeps them running. Until it is planned, a
/// resource runs as the previous run left it. Every created or updated resource must have an
/// instance publish within <see cref="StartupTimeout"/> of its deployment: a new one that does not is
/// torn down, an updated one is left to the platform, what depends on it is not deployed, and the run
/// fails with <see cref="ApplicationGatewayErrorCode.StartupTimedOut"/>. After startup the gateway
/// tears down the resources its record holds but the model no longer declares, then runs until
/// cancelled. Cancellation stops the gateway; it does not tear down what the gateway deployed.
/// </para>
/// <para>
/// The gateway saves its record after every change, so a gateway restarted with the same model
/// adopts what runs instead of deploying it again. Every registration carries a lease of
/// <see cref="LeaseDuration"/>: an instance renews it (<see cref="RenewAsync"/>), and one that stops
/// renewing is unregistered once it lapses. After startup every registered instance, the adopted
/// ones included, has one full lease to renew, so an instance that died while the gateway was down
/// leaves within one lease. The gateway is transport-neutral: a transport hands each publication to
/// <see cref="PublishAsync"/> and each renewal to <see cref="RenewAsync"/>, as the
/// <see cref="IApplicationInstancePublisher"/> it is. The transports the gateway was built with
/// (<see cref="ApplicationBuilder.UseTransport"/>) start once <see cref="RunAsync"/> has loaded the
/// record, before anything is joined or deployed, and stop when the run ends.
/// </para>
/// <para>
/// A child gateway joins through <see cref="JoinAsync"/>, which this gateway implements as an
/// <see cref="IApplicationParentGateway"/>; the child joins again three times per lease, and one that
/// stops is dropped once its lease lapses. The gateway knows nothing of a child before it joins, so
/// each gateway deploys on its own. A value a child read changes for the child on its next run.
/// </para>
/// </remarks>
[DebuggerDisplay("{Name}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ApplicationGateway : IApplicationGateway, IApplicationInstancePublisher, IApplicationParentGateway
{
    private const int idle = 0;
    private const int running = 1;
    private const int stopped = 2;

    private static readonly TimeSpan _minimumExpiryInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan _maximumJoinRetryInterval = TimeSpan.FromMilliseconds(250);

    // The UUIDv5 namespace of deployment ids. Only the gateway that derives an id compares it, so it
    // is not wire contract, but its own namespace keeps a deployment id apart from every other id.
    private static readonly Guid _deploymentNamespace = new("c7e50fe9-ea03-4156-a9fe-9748a45e60ca");

    private readonly IApplicationModel _model;
    private readonly IInfrastructureProvider _infrastructure;
    private readonly IApplicationGatewayStore _store;
    private readonly IApplicationParentGateway? _parent;
    private readonly IApplicationGatewayTransport[] _transports;
    private readonly SemaphoreSlim _saves = new(1, 1);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<GatewayName, (ApplicationManifest Manifest, long Renewed)> _children = [];
    private FrozenDictionary<(ProviderName Provider, ResourceName Name), GatewayResource>? _resources;
    private FrozenDictionary<ApplicationResourceOutput, string> _received = FrozenDictionary<ApplicationResourceOutput, string>.Empty;
    private GatewayResource[] _ordered = [];
    private ApplicationResourceRecord[] _retiredResources = [];
    private ReadOnlyCollection<IApplicationResourceDeployment> _aggregates = ReadOnlyCollection<IApplicationResourceDeployment>.Empty;
    private int _lifecycle;

    internal ApplicationGateway(
        IApplicationModel model,
        IInfrastructureProvider infrastructure,
        IApplicationGatewayStore store,
        IApplicationParentGateway? parent,
        IApplicationGatewayTransport[] transports,
        TimeSpan startupTimeout,
        TimeSpan leaseDuration)
    {
        _model = model;
        _infrastructure = infrastructure;
        _store = store;
        _parent = parent;
        _transports = transports;
        StartupTimeout = startupTimeout;
        LeaseDuration = leaseDuration;
    }

    /// <summary>
    /// Gets the gateway's name, which its model declares.
    /// </summary>
    public GatewayName Name => _model.Name;

    /// <summary>
    /// Gets the model the gateway runs.
    /// </summary>
    public IApplicationModel Model => _model;

    /// <summary>
    /// Gets how long every created or updated resource has to publish its first manifest once its
    /// deployment starts, and how long the gateway waits for its parent's outputs.
    /// </summary>
    public TimeSpan StartupTimeout { get; }

    /// <summary>
    /// Gets how long an instance or a child gateway stays registered without renewing its lease.
    /// </summary>
    public TimeSpan LeaseDuration { get; }

    /// <summary>
    /// Gets the aggregates of the resources the gateway runs, in dependency order, each after the
    /// resources it depends on; empty before <see cref="RunAsync"/>.
    /// </summary>
    public IReadOnlyCollection<IApplicationResourceDeployment> Resources => Volatile.Read(ref _aggregates);

    /// <summary>
    /// Gets the manifests of the child gateways that joined this one and whose leases have not
    /// lapsed.
    /// </summary>
    public IReadOnlyCollection<ApplicationManifest> Children
    {
        get
        {
            lock (_children)
            {
                var children = new List<ApplicationManifest>(_children.Count);
                foreach ((ApplicationManifest Manifest, long Renewed) child in _children.Values)
                {
                    children.Add(child.Manifest);
                }

                return children.AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Gets a task that completes when every created or updated resource has published and the
    /// resources the model no longer declares are torn down, faults when startup fails, and is
    /// canceled when the run is cancelled before startup completes.
    /// </summary>
    public Task Started => _started.Task;

    /// <summary>
    /// Creates a builder that declares a gateway's application and configures the gateway.
    /// </summary>
    /// <param name="name">The gateway's name.</param>
    /// <returns>The builder.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    public static ApplicationBuilder CreateBuilder(GatewayName name)
    {
        return new ApplicationBuilder(name);
    }

    /// <summary>
    /// Reconciles the model with the infrastructure and runs the gateway until cancellation.
    /// </summary>
    /// <param name="cancellationToken">Signals that the gateway should stop.</param>
    /// <returns>A task that completes when the gateway stops.</returns>
    /// <exception cref="ArgumentException">The parent refuses this gateway's manifest. Nothing has been deployed.</exception>
    /// <exception cref="InvalidOperationException">
    /// The gateway has already run, or the infrastructure provider returned no action list, no plan,
    /// or no outputs.
    /// </exception>
    /// <exception cref="ApplicationGatewayException">
    /// The parent did not accept this gateway, or did not report an output its settings use, within
    /// <see cref="StartupTimeout"/> (<see cref="ApplicationGatewayErrorCode.StartupTimedOut"/> or
    /// <see cref="ApplicationGatewayErrorCode.OutputMissing"/>); a created or updated resource
    /// published no manifest within <see cref="StartupTimeout"/> of its deployment
    /// (<see cref="ApplicationGatewayErrorCode.StartupTimedOut"/>); or a setting uses an output its
    /// resource's provider did not report (<see cref="ApplicationGatewayErrorCode.OutputMissing"/>).
    /// </exception>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _lifecycle, running, idle) != idle)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationGatewayAlreadyRun);
        }

        int transportsStarted = 0;
        bool failed = false;
        try
        {
            IApplicationModel model = _model;
            GatewayStep[] steps = GatewayStep.Order(model.Manifest);

            ApplicationGatewayRecord? record = await _store.LoadAsync(model.Name, cancellationToken).ConfigureAwait(false);
            CreateResources(model, record, steps);

            // The gateway receives publications from here, so its transports start before it joins
            // its parent or deploys: an instance the previous run left can publish meanwhile.
            foreach (IApplicationGatewayTransport transport in _transports)
            {
                await transport.StartAsync(this, cancellationToken).ConfigureAwait(false);
                transportsStarted++;
            }

            // A child fills its settings in from its parent's outputs, so it joins before anything
            // deploys; its restarted instances can publish meanwhile.
            ApplicationGatewayMembership? membership = null;
            if (_parent is not null)
            {
                membership = await JoinParentAsync(model, _parent, cancellationToken).ConfigureAwait(false);
                _received = Receive(membership);
            }

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

            Task renewals = membership is null
                ? Task.CompletedTask
                : RenewMembershipAsync(model, _parent!, membership.Lease, cancellationToken);
            await Task.WhenAll(ExpireAsync(cancellationToken), renewals).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation stops the gateway; what it deployed keeps running.
            _started.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            // Every other failure ends the run; Started reports it before the caller sees it.
            failed = true;
            _started.TrySetException(exception);
            throw;
        }
        finally
        {
            Volatile.Write(ref _lifecycle, stopped);
            await StopTransportsAsync(transportsStarted, failed).ConfigureAwait(false);
        }
    }

    // Stops the transports that started, last first. One that fails to stop does not keep the others
    // running. Its failure is the run's unless the run already failed, whose own failure it would hide.
    private async Task StopTransportsAsync(int started, bool failed)
    {
        List<Exception>? failures = null;
        for (int i = started - 1; i >= 0; i--)
        {
            try
            {
                await _transports[i].StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Deliberately broad: each transport fails to stop in its own terms.
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null && !failed)
        {
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
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
    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        GatewayResource resource = GetResource(manifest.Provider, manifest.Name);
        if (resource.IsClosed)
        {
            throw Closed(resource);
        }

        IReadOnlyList<ApplicationActionSnapshot> restore = resource.Register(manifest);
        if (!resource.TryMarkRegistered(manifest))
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
    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
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
    /// Registers a child gateway, or renews its registration, and answers with the values of this
    /// gateway's outputs that the child's settings use.
    /// </summary>
    /// <remarks>
    /// The answer holds every value recorded so far; an output whose resource is not ready yet is
    /// absent, and the child joins again. A child that joins under the name of one already joined
    /// replaces it.
    /// </remarks>
    /// <param name="child">The child's manifest: its name, this gateway as its parent, and its resources.</param>
    /// <param name="cancellationToken">Unused; joining completes synchronously.</param>
    /// <returns>The values the child uses, and a lease of <see cref="LeaseDuration"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="child"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The manifest breaks a rule of <see cref="ApplicationModel.Create(ApplicationManifest)"/>, or
    /// names another gateway as its parent.
    /// </exception>
    /// <exception cref="ApplicationGatewayException">
    /// The gateway is not running (<see cref="ApplicationGatewayErrorCode.PublicationsClosed"/>).
    /// </exception>
    public ValueTask<ApplicationGatewayMembership> JoinAsync(ApplicationManifest child, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(child);

        ApplicationModel joined = ApplicationModel.Create(child);
        if (joined.Parent != Name)
        {
            throw new ArgumentException(Format(ErrorMessages.GatewayParentMismatch, joined.Name, joined.Parent?.ToString() ?? string.Empty, Name), nameof(child));
        }

        if (Volatile.Read(ref _resources) is null || Volatile.Read(ref _lifecycle) != running)
        {
            throw new ApplicationGatewayException(ApplicationGatewayErrorCode.PublicationsClosed, ErrorMessages.GatewayPublicationsClosed);
        }

        lock (_children)
        {
            _children[joined.Name] = (joined.Manifest, Stopwatch.GetTimestamp());
        }

        var values = new List<ApplicationResourceOutputValue>();
        var named = new HashSet<ApplicationResourceOutput>();
        foreach (ApplicationResourceManifest resource in joined.Manifest.Resources)
        {
            foreach (ApplicationResourceSetting setting in resource.Settings)
            {
                if (setting.Output is { } output && output.Gateway == Name && named.Add(output) && TryGetOutput(output, out string? value))
                {
                    values.Add(new ApplicationResourceOutputValue(output, value));
                }
            }
        }

        return ValueTask.FromResult(new ApplicationGatewayMembership(values.AsReadOnly(), LeaseDuration));
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
    public ValueTask<IReadOnlyList<InstanceKey>> RecordAsync(
        ProviderName provider,
        ResourceName name,
        InstanceKey source,
        ApplicationActionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IReadOnlyList<InstanceKey> targets = GetResource(provider, name).Aggregate.Record(source, snapshot);
        return SaveRecordingAsync(targets, cancellationToken);
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
        FrozenDictionary<(ProviderName Provider, ResourceName Name), GatewayResource>? resources = Volatile.Read(ref _resources);
        if (resources is null || output.Gateway != Name)
        {
            return false;
        }

        return resources.TryGetValue((output.Provider, output.Name), out GatewayResource? resource)
            && resource.Aggregate.Outputs.TryGetValue(output.Output, out value);
    }

    private async ValueTask<ApplicationInstancePublication> SaveRegistrationAsync(
        GatewayResource resource,
        InstanceKey key,
        IReadOnlyList<ApplicationActionSnapshot> restore,
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

        return new ApplicationInstancePublication(restore, LeaseDuration);
    }

    private async ValueTask<IReadOnlyList<InstanceKey>> SaveRecordingAsync(IReadOnlyList<InstanceKey> targets, CancellationToken cancellationToken)
    {
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return targets;
    }

    // Joins the parent until it reports every output of its resources that the gateway's settings
    // use, retrying while the parent is not running or a resource of its is not ready yet, within the
    // startup window.
    private async Task<ApplicationGatewayMembership> JoinParentAsync(IApplicationModel model, IApplicationParentGateway parent, CancellationToken cancellationToken)
    {
        var needed = new List<(ApplicationResourceManifest Resource, ApplicationResourceSetting Setting, ApplicationResourceOutput Output)>();
        foreach (ApplicationResourceManifest resource in model.Manifest.Resources)
        {
            foreach (ApplicationResourceSetting setting in resource.Settings)
            {
                if (setting.Output is { } output && output.Gateway != model.Name)
                {
                    needed.Add((resource, setting, output));
                }
            }
        }

        TimeSpan retry = StartupTimeout / 4;
        if (retry > _maximumJoinRetryInterval)
        {
            retry = _maximumJoinRetryInterval;
        }

        if (retry < _minimumExpiryInterval)
        {
            retry = _minimumExpiryInterval;
        }

        long started = Stopwatch.GetTimestamp();
        (ApplicationResourceManifest Resource, ApplicationResourceSetting Setting, ApplicationResourceOutput Output)? missing = null;
        bool joined = false;
        while (true)
        {
            try
            {
                ApplicationGatewayMembership membership = await parent.JoinAsync(model.Manifest, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(Format(ErrorMessages.GatewayParentMembershipNull, parent.Name));
                joined = true;
                missing = FindMissing(needed, membership);
                if (missing is null)
                {
                    return membership;
                }
            }
            catch (ApplicationGatewayException exception) when (exception.ErrorCode == ApplicationGatewayErrorCode.PublicationsClosed)
            {
                // The parent is not running yet.
            }

            if (Stopwatch.GetElapsedTime(started) >= StartupTimeout)
            {
                break;
            }

            await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
        }

        if (!joined)
        {
            throw new ApplicationGatewayException(
                ApplicationGatewayErrorCode.StartupTimedOut,
                Format(ErrorMessages.GatewayParentUnavailable, model.Name, parent.Name, StartupTimeout));
        }

        (ApplicationResourceManifest unresolved, ApplicationResourceSetting waiting, ApplicationResourceOutput absent) = missing!.Value;
        throw new ApplicationGatewayException(
            ApplicationGatewayErrorCode.OutputMissing,
            Format(ErrorMessages.GatewayParentOutputMissing, waiting.Name, unresolved.Provider, unresolved.Name, model.Name, absent.Output, absent.Provider, absent.Name, absent.Gateway, StartupTimeout));
    }

    private static (ApplicationResourceManifest Resource, ApplicationResourceSetting Setting, ApplicationResourceOutput Output)? FindMissing(
        List<(ApplicationResourceManifest Resource, ApplicationResourceSetting Setting, ApplicationResourceOutput Output)> needed,
        ApplicationGatewayMembership membership)
    {
        if (membership.Outputs is null)
        {
            return needed.Count == 0 ? null : needed[0];
        }

        var reported = new HashSet<ApplicationResourceOutput>();
        foreach (ApplicationResourceOutputValue? value in membership.Outputs)
        {
            if (value?.Output is not null && value.Value is not null)
            {
                reported.Add(value.Output);
            }
        }

        foreach ((ApplicationResourceManifest Resource, ApplicationResourceSetting Setting, ApplicationResourceOutput Output) entry in needed)
        {
            if (!reported.Contains(entry.Output))
            {
                return entry;
            }
        }

        return null;
    }

    // The values the parent reported, by output.
    private static FrozenDictionary<ApplicationResourceOutput, string> Receive(ApplicationGatewayMembership membership)
    {
        var received = new Dictionary<ApplicationResourceOutput, string>();
        foreach (ApplicationResourceOutputValue? value in membership.Outputs ?? [])
        {
            if (value?.Output is not null && value.Value is not null)
            {
                received[value.Output] = value.Value;
            }
        }

        return received.ToFrozenDictionary();
    }

    // Joins the parent again three times per lease, so it keeps this gateway registered. A parent
    // that is unreachable, or not running, is retried on the next tick; a value that changed applies
    // on the gateway's next run.
    private static async Task RenewMembershipAsync(IApplicationModel model, IApplicationParentGateway parent, TimeSpan lease, CancellationToken cancellationToken)
    {
        TimeSpan interval = lease / 3;
        if (interval < _minimumExpiryInterval)
        {
            interval = _minimumExpiryInterval;
        }

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await parent.JoinAsync(model.Manifest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Deliberately broad: each transport reports an unreachable or refusing parent in its
                // own terms. The gateway keeps running and joins again on the next tick.
            }
        }
    }

    // Unregisters every instance and drops every child gateway whose lease lapsed, three times per
    // lease, until the run is cancelled.
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
            ExpireChildren();

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

    // Children are not recorded: a child that is still running joins a restarted parent again.
    private void ExpireChildren()
    {
        lock (_children)
        {
            var lapsed = new List<GatewayName>();
            foreach (KeyValuePair<GatewayName, (ApplicationManifest Manifest, long Renewed)> child in _children)
            {
                if (Stopwatch.GetElapsedTime(child.Value.Renewed) >= LeaseDuration)
                {
                    lapsed.Add(child.Key);
                }
            }

            foreach (GatewayName name in lapsed)
            {
                _children.Remove(name);
            }
        }
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
                Format(ErrorMessages.GatewayResourceUnknown, Name, provider, name));
        }

        return resource;
    }

    private void CreateResources(IApplicationModel model, ApplicationGatewayRecord? record, GatewayStep[] steps)
    {
        var recorded = new Dictionary<(ProviderName Provider, ResourceName Name), ApplicationResourceRecord>();
        foreach (ApplicationResourceRecord entry in record?.Resources ?? [])
        {
            recorded[(entry.Provider, entry.Name)] = entry;
        }

        var lookup = new Dictionary<(ProviderName Provider, ResourceName Name), GatewayResource>();
        foreach (ApplicationResourceManifest manifest in model.Manifest.Resources)
        {
            // The provider sees a resource as declared for its platform: no other platform's
            // extensions reach it.
            ApplicationResourceManifest declared = ForPlatform(manifest);
            IReadOnlyCollection<IApplicationResourceAction> actions = _infrastructure.GetResourceActions(model, declared)
                ?? throw new InvalidOperationException(Format(ErrorMessages.GatewayResourceActionsNull, manifest.Provider, manifest.Name));

            // Until its step plans it, a resource runs as the previous run left it: its recorded
            // state, outputs, and instances. The plan then keeps what still applies.
            recorded.Remove((manifest.Provider, manifest.Name), out ApplicationResourceRecord? previous);
            ApplicationResourceDeployment aggregate = ApplicationResourceDeployment.Create(manifest, previous?.State, actions, previous?.Outputs);
            lookup.Add((manifest.Provider, manifest.Name), new GatewayResource(aggregate, declared, previous));
        }

        // The resources are kept in the order they deploy in.
        var ordered = new List<GatewayResource>();
        var aggregates = new List<IApplicationResourceDeployment>();
        foreach (GatewayStep step in steps)
        {
            GatewayResource resource = lookup[(step.Resource.Provider, step.Resource.Name)];
            ordered.Add(resource);
            aggregates.Add(resource.Aggregate);
        }

        // What the record holds and the model no longer declares is retired after startup.
        var retiredResources = new List<ApplicationResourceRecord>();
        foreach (ApplicationResourceRecord entry in record?.Resources ?? [])
        {
            if (recorded.ContainsKey((entry.Provider, entry.Name)))
            {
                retiredResources.Add(entry);
            }
        }

        _ordered = [.. ordered];
        Volatile.Write(ref _retiredResources, [.. retiredResources]);
        Volatile.Write(ref _aggregates, aggregates.AsReadOnly());

        // Publications and joins are accepted from here, before any resource is deployed.
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
            else if (Find(step.Resource).IsClosed)
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
            ready = await RunResourceAsync(model, Find(step.Resource), walk.Token).ConfigureAwait(false);
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
        // dependency or the parent is not part of it, so a dependency that moved does not cost this
        // resource its recorded state, and neither does an extension for another platform. The
        // deployment adds the values: only an instance that repeats its id makes the resource ready.
        // Planned before the call: an in-process provider's instances publish before it returns.
        string revision = string.Concat(Fingerprint(manifest), ":", plan.Revision);
        DeploymentId? deployment = plan.Change == InfrastructureChange.None ? null : DeploymentOf(revision, manifest, settings);
        resource.Plan(plan.Change, revision, deployment);
        if (deployment is { } id)
        {
            await _infrastructure.DeployResourceAsync(model, manifest, settings, id, cancellationToken).ConfigureAwait(false);
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
        var settings = new Dictionary<SettingName, string>(manifest.Settings.Count);
        foreach (ApplicationResourceSetting setting in manifest.Settings)
        {
            settings.Add(setting.Name, setting.Output is { } output ? ResolveOutput(manifest, setting, output) : setting.Value!);
        }

        return settings.AsReadOnly();
    }

    // The value of an output a setting uses. One of this gateway's resources is ready by the time
    // anything that depends on it is planned or deployed, so its outputs have been read; any other is
    // the parent's, which reported it when this gateway joined.
    private string ResolveOutput(ApplicationResourceManifest resource, ApplicationResourceSetting setting, ApplicationResourceOutput output)
    {
        if (output.Gateway != Name)
        {
            return _received[output];
        }

        if (!Volatile.Read(ref _resources)![(output.Provider, output.Name)].Aggregate.Outputs.TryGetValue(output.Output, out string? value))
        {
            throw new ApplicationGatewayException(
                ApplicationGatewayErrorCode.OutputMissing,
                Format(ErrorMessages.GatewaySettingOutputMissing, setting.Name, resource.Provider, resource.Name, Name, output.Output, output.Provider, output.Name));
        }

        return value;
    }

    private GatewayResource Find(ApplicationResourceManifest manifest)
    {
        return Volatile.Read(ref _resources)![(manifest.Provider, manifest.Name)];
    }

    private async Task RetireAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        foreach (ApplicationResourceRecord retired in Volatile.Read(ref _retiredResources))
        {
            await _infrastructure.TeardownResourceAsync(model, new ApplicationResourceManifest(retired.Provider, retired.Name, [], [], []), cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _retiredResources, Array.FindAll(Volatile.Read(ref _retiredResources), entry => !ReferenceEquals(entry, retired)));
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

    private ApplicationGatewayRecord CreateRecord()
    {
        var resources = new List<ApplicationResourceRecord>();
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

            ApplicationResourceDeployment aggregate = resource.Aggregate;
            resources.Add(new ApplicationResourceRecord(
                aggregate.Manifest.Provider,
                aggregate.Manifest.Name,
                resource.Revision!,
                [.. aggregate.State],
                [.. aggregate.Instances],
                aggregate.Outputs));
        }

        // A retired resource stays recorded until its teardown succeeds, so a failed run retries it.
        resources.AddRange(Volatile.Read(ref _retiredResources));

        return new ApplicationGatewayRecord(Name, resources.AsReadOnly());
    }

    // The resource's definition in the model, as the JSON of its manifest as declared for this
    // platform: provider, name, dependencies, settings, and this platform's extensions.
    private static string Fingerprint(ApplicationResourceManifest manifest)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, GatewayJsonContext.Default.ApplicationResourceManifest);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    // A deployment is what the gateway deploys a resource with: its revision (its definition and the
    // provider's revision) and its settings' values, in the manifest's order. Each value is prefixed
    // with its length, so no two sets of values read alike. The same inputs give the same id, so a
    // resource deployed again with them starts instances that repeat the id running ones report.
    private static DeploymentId DeploymentOf(string revision, ApplicationResourceManifest manifest, IReadOnlyDictionary<SettingName, string> settings)
    {
        var content = new StringBuilder(revision);
        foreach (ApplicationResourceSetting setting in manifest.Settings)
        {
            string value = settings[setting.Name];
            content.Append('\n')
                .Append(setting.Name.Value)
                .Append('=')
                .Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value);
        }

        return Guid.AsDeterministicGuid(content.ToString(), _deploymentNamespace);
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
            Format(format, Name, manifest.Provider, manifest.Name));
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

        public IApplicationModel Model => _gateway.Model;

        public TimeSpan StartupTimeout => _gateway.StartupTimeout;

        public IReadOnlyCollection<IApplicationResourceDeployment> Resources => _gateway.Resources;

        public IReadOnlyCollection<ApplicationManifest> Children => _gateway.Children;
    }
}
