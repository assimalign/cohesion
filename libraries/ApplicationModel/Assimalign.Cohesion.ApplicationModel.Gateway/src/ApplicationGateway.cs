using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
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
/// <para>
/// An operator manages a resource through <see cref="ExecuteAsync"/>: the gateway runs the action on
/// one instance's control plane, through its <see cref="IApplicationInstanceClient"/>, and keeps a
/// write: it records the state the write left and restores it on the other instances, and later
/// instances restore it when they publish.
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
    private readonly IApplicationInstanceClient? _client;
    private readonly SemaphoreSlim _saves = new(1, 1);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<GatewayName, (ApplicationManifest Manifest, long Renewed)> _children = [];
    private FrozenDictionary<(ProviderName Provider, ResourceName Name), GatewayResource>? _resources;
    private FrozenDictionary<ApplicationResourceOutput, string> _received = FrozenDictionary<ApplicationResourceOutput, string>.Empty;
    private GatewayResource[] _ordered = [];
    private ApplicationResourceRecord[] _retiredResources = [];
    private ReadOnlyCollection<IApplicationResourceDeployment> _aggregates = ReadOnlyCollection<IApplicationResourceDeployment>.Empty;
    private long _epoch;
    private CancellationTokenSource? _run;
    private ApplicationGatewayException? _superseded;
    private int _signalled;
    private int _lifecycle;

    internal ApplicationGateway(
        IApplicationModel model,
        IInfrastructureProvider infrastructure,
        IApplicationGatewayStore store,
        IApplicationParentGateway? parent,
        IApplicationGatewayTransport[] transports,
        IApplicationInstanceClient? client,
        TimeSpan startupTimeout,
        TimeSpan leaseDuration)
    {
        _model = model;
        _infrastructure = infrastructure;
        _store = store;
        _parent = parent;
        _transports = transports;
        _client = client;
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
    /// (<see cref="ApplicationGatewayErrorCode.StartupTimedOut"/>); a created or updated resource's
    /// workload stopped before an instance published, with the workload's failure as the inner
    /// exception (<see cref="ApplicationGatewayErrorCode.ResourceFailed"/>); a setting uses an output
    /// its resource's provider did not report (<see cref="ApplicationGatewayErrorCode.OutputMissing"/>);
    /// or a later run of this gateway took its record over, so this run stopped
    /// (<see cref="ApplicationGatewayErrorCode.RecordSuperseded"/>).
    /// </exception>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _lifecycle, running, idle) != idle)
        {
            throw new InvalidOperationException(ErrorMessages.ApplicationGatewayAlreadyRun);
        }

        // The run's own token stops it when the caller cancels, a shutdown signal arrives, or a later
        // run takes the record over. A signal stops the run as cancelling it does: Ctrl+C at a console
        // or a platform's SIGTERM drains the gateway instead of ending the process mid-save. Inside an
        // in-process program's scope, the scope's shutdown stands in for the signals.
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable signals = AppEnvironment.RegisterShutdown(() => RequestShutdown(run));
        Volatile.Write(ref _run, run);
        CancellationToken token = run.Token;
        int transportsStarted = 0;
        bool failed = false;
        try
        {
            IApplicationModel model = _model;
            GatewayStep[] steps = GatewayStep.Order(model.Manifest);

            // Taking the record over fences off every earlier run of this gateway: the store refuses
            // their saves from here, so the newest run wins and the older one stops.
            ApplicationGatewayRecord record = await _store.AcquireAsync(model.Name, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException(Format(ErrorMessages.GatewayRecordNull, model.Name));
            Volatile.Write(ref _epoch, record.Epoch);
            ApplicationGatewayEventSource.Log.RunStarting(model.Name, record.Epoch);
            CreateResources(model, record, steps);

            // The gateway receives publications from here, so its transports start before it joins
            // its parent or deploys: an instance the previous run left can publish meanwhile.
            foreach (IApplicationGatewayTransport transport in _transports)
            {
                await transport.StartAsync(this, token).ConfigureAwait(false);
                transportsStarted++;
            }

            // A child fills its settings in from its parent's outputs, so it joins before anything
            // deploys; its restarted instances can publish meanwhile.
            ApplicationGatewayMembership? membership = null;
            if (_parent is not null)
            {
                membership = await JoinParentAsync(model, _parent, token).ConfigureAwait(false);
                _received = Receive(membership);
            }

            await DeployAsync(model, steps, token).ConfigureAwait(false);

            // Retire what the model no longer declares only once the deployment has started, so a
            // failed deployment leaves the previous one's resources running.
            await RetireAsync(model, token).ConfigureAwait(false);

            // Every registered instance, the adopted ones included, has one full lease from here.
            foreach (GatewayResource resource in _ordered)
            {
                resource.RenewAll();
            }

            _started.TrySetResult();
            ApplicationGatewayEventSource.Log.RunStarted(model.Name, _ordered.Length);

            Task renewals = membership is null
                ? Task.CompletedTask
                : RenewMembershipAsync(model, _parent!, membership.Lease, token);
            await Task.WhenAll(ExpireAsync(token), renewals).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _superseded) is null && (cancellationToken.IsCancellationRequested || Volatile.Read(ref _signalled) != 0))
        {
            // Cancellation or a shutdown signal stops the gateway; what it deployed keeps running.
            _started.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : token);
            ApplicationGatewayEventSource.Log.RunStopped(Name);
        }
        catch (Exception exception)
        {
            // Every other failure ends the run; Started reports it before the caller sees it. A run a
            // later one superseded reports that, whatever the supersession interrupted.
            failed = true;
            Exception reported = Volatile.Read(ref _superseded) ?? exception;
            _started.TrySetException(reported);
            ApplicationGatewayEventSource.Log.RunFailed(Name, reported);
            ExceptionDispatchInfo.Throw(reported);
        }
        finally
        {
            Volatile.Write(ref _lifecycle, stopped);
            Volatile.Write(ref _run, null);
            await StopTransportsAsync(transportsStarted, failed).ConfigureAwait(false);
        }
    }

    // Ends this run once a later run of the gateway took the record over. This one can no longer
    // save it, so it takes no more publications or joins, and its run stops with the reason.
    private ApplicationGatewayException Supersede()
    {
        var superseded = new ApplicationGatewayException(
            ApplicationGatewayErrorCode.RecordSuperseded,
            Format(ErrorMessages.GatewayRecordSuperseded, Name, Volatile.Read(ref _epoch)));
        if (Interlocked.CompareExchange(ref _superseded, superseded, null) is { } earlier)
        {
            return earlier;
        }

        ApplicationGatewayEventSource.Log.RunSuperseded(Name, Volatile.Read(ref _epoch));
        Volatile.Write(ref _lifecycle, stopped);
        try
        {
            // Asynchronously: whatever waits on the run's token continues on the thread pool, not
            // on the publication or write that found the record taken over.
            _ = Volatile.Read(ref _run)?.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The run already ended.
        }

        return superseded;
    }

    // A shutdown signal, or in process the scope's shutdown, stops the run as cancelling it does. It
    // never throws: a signal handler that throws ends the process.
    private void RequestShutdown(CancellationTokenSource run)
    {
        Volatile.Write(ref _signalled, 1);
        try
        {
            _ = run.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The run already ended.
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
    /// not run the resource (<see cref="ApplicationGatewayErrorCode.ResourceUnknown"/>), stopped
    /// accepting it after it missed its startup window (<see cref="ApplicationGatewayErrorCode.ResourceTornDown"/>),
    /// or found while saving the registration that a later run took its record over
    /// (<see cref="ApplicationGatewayErrorCode.RecordSuperseded"/>).
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
    /// <remarks>
    /// <see cref="ExecuteAsync"/> runs a write through the gateway and restores it on the other
    /// instances itself. This method records a write made on an instance some other way, and leaves
    /// the restores to its caller.
    /// </remarks>
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
    /// The gateway is not running (<see cref="ApplicationGatewayErrorCode.PublicationsClosed"/>), does
    /// not run the resource (<see cref="ApplicationGatewayErrorCode.ResourceUnknown"/>), or found while
    /// saving that a later run took its record over (<see cref="ApplicationGatewayErrorCode.RecordSuperseded"/>).
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
    /// Runs an operator's action on an instance of a resource. A successful write to a stateful action
    /// is recorded, the record saved, and the write's state restored on every other instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The action runs on one registered instance whose manifest declares it and names a control
    /// plane, the earliest started first, since one that registered a moment ago may not serve its
    /// control plane yet. The gateway calls it through its <see cref="IApplicationInstanceClient"/>.
    /// An instance the client gets no answer from is unregistered, and the action runs on the next
    /// one; an instance that still runs finds itself unknown when it next renews, and publishes again.
    /// </para>
    /// <para>
    /// After a successful write to a stateful action, the gateway records the snapshot the instance
    /// answered with, as <see cref="RecordAsync"/> does, saves the record, and restores the snapshot
    /// on every other registered instance that declares the action stateful, all at once. An instance
    /// that cannot be reached, does not restore it, or names no control plane is unregistered: when
    /// it next renews it publishes again, and the answer carries the recorded state. Instances that
    /// start later restore it from their publication's answer. The call returns once every restore
    /// has ended. Writes to a resource run one at a time, from the call to the instance to the last
    /// restore, so every instance ends with the state the record holds; reads run at any time.
    /// </para>
    /// </remarks>
    /// <param name="provider">The resource's provider.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="action">The action's name.</param>
    /// <param name="context">The invocation: the operation and its input, written by the context's serializer.</param>
    /// <param name="cancellationToken">
    /// Cancels the invocation, the save, and the restores; an instance whose restore it cancels is
    /// unregistered, so it restores the write when it publishes again.
    /// </param>
    /// <returns>
    /// What the instance answered, or <see cref="ApplicationActionStatus.NotFound"/> when no registered
    /// instance declares the action.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The instance client returned no result, or the instance a write ran on left before the gateway
    /// recorded the write.
    /// </exception>
    /// <exception cref="ApplicationGatewayException">
    /// The gateway is not running (<see cref="ApplicationGatewayErrorCode.PublicationsClosed"/>), does
    /// not run the resource (<see cref="ApplicationGatewayErrorCode.ResourceUnknown"/>), stopped
    /// accepting it after it missed its startup window (<see cref="ApplicationGatewayErrorCode.ResourceTornDown"/>),
    /// has no instance client or reached no registered instance that declares the action
    /// (<see cref="ApplicationGatewayErrorCode.InstanceUnavailable"/>), or found while saving the
    /// write that a later run took its record over (<see cref="ApplicationGatewayErrorCode.RecordSuperseded"/>).
    /// </exception>
    public async ValueTask<ApplicationActionResult> ExecuteAsync(
        ProviderName provider,
        ResourceName name,
        ActionName action,
        ApplicationActionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        GatewayResource resource = GetResource(provider, name);
        if (resource.IsClosed)
        {
            throw Closed(resource);
        }

        // The platform owns the call to an instance; a gateway built without its client has none.
        IApplicationInstanceClient client = _client ?? throw new ApplicationGatewayException(
            ApplicationGatewayErrorCode.InstanceUnavailable,
            Format(ErrorMessages.GatewayInstanceClientMissing, Name, provider, name, action),
            null);

        // Writes run one at a time, from the call to the instance to the last restore, so a restore of
        // an earlier write never lands on an instance after a later write's.
        if (context.Operation != ApplicationActionKind.Write)
        {
            return await RunActionAsync(resource, client, provider, name, action, context, cancellationToken).ConfigureAwait(false);
        }

        await resource.Writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunActionAsync(resource, client, provider, name, action, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            resource.Writes.Release();
        }
    }

    private async Task<ApplicationActionResult> RunActionAsync(
        GatewayResource resource,
        IApplicationInstanceClient client,
        ProviderName provider,
        ResourceName name,
        ActionName action,
        ApplicationActionContext context,
        CancellationToken cancellationToken)
    {
        // Instances are ordered by key, so by start time: the earliest started is called first.
        IReadOnlyCollection<ApplicationInstanceManifest> instances = resource.Aggregate.Instances;
        var candidates = new List<ApplicationInstanceManifest>(instances.Count);
        bool declared = false;
        foreach (ApplicationInstanceManifest instance in instances)
        {
            if (Declares(instance, action))
            {
                declared = true;
                if (instance.ControlPlaneEndpoint is not null)
                {
                    candidates.Add(instance);
                }
            }
        }

        if (instances.Count > 0 && !declared)
        {
            return ApplicationActionResult.NotFound();
        }

        // An instance unregistered here leaves the record with the next save; it publishes again when
        // it next renews.
        List<Exception>? failures = null;
        foreach (ApplicationInstanceManifest instance in candidates)
        {
            ApplicationActionResult? result;
            try
            {
                result = await client.ExecuteAsync(instance, action, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Deliberately broad: each client reports an instance it gets no answer from in its
                // own terms. The instance is treated as gone, and the next one is called.
                (failures ??= []).Add(exception);
                Drop(resource, instance, exception, refusal: null);
                continue;
            }

            if (result is null)
            {
                throw new InvalidOperationException(ErrorMessages.InstanceClientResultNull);
            }

            ApplicationGatewayEventSource.Log.ActionExecuted(Name, provider, name, action, context.Operation, instance.Key, result.Status);
            if (context.Operation == ApplicationActionKind.Write && result.Status == ApplicationActionStatus.Succeeded && result.State is { } snapshot)
            {
                IReadOnlyList<InstanceKey> targets = resource.Aggregate.Record(instance.Key, snapshot);
                ApplicationGatewayEventSource.Log.StateRecorded(Name, provider, name, snapshot.Action, instance.Key, targets.Count);
                await SaveAsync(cancellationToken).ConfigureAwait(false);
                if (await RestoreAsync(resource, client, targets, snapshot, cancellationToken).ConfigureAwait(false))
                {
                    await SaveAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else if (failures is not null)
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        if (failures is not null)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }

        throw new ApplicationGatewayException(
            ApplicationGatewayErrorCode.InstanceUnavailable,
            Format(ErrorMessages.GatewayInstanceUnavailable, Name, provider, name, action),
            failures switch
            {
                null => null,
                [Exception failure] => failure,
                _ => new AggregateException(failures),
            });
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

    // Restores a write's snapshot on the instances the aggregate named, all at once, and returns
    // whether any was unregistered because the restore did not reach it.
    private async Task<bool> RestoreAsync(GatewayResource resource, IApplicationInstanceClient client, IReadOnlyList<InstanceKey> targets, ApplicationActionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return false;
        }

        var registered = new Dictionary<InstanceKey, ApplicationInstanceManifest>();
        foreach (ApplicationInstanceManifest instance in resource.Aggregate.Instances)
        {
            registered[instance.Key] = instance;
        }

        var restores = new List<Task<bool>>(targets.Count);
        foreach (InstanceKey target in targets)
        {
            // One that left since the write was recorded restores it when it publishes again.
            if (registered.TryGetValue(target, out ApplicationInstanceManifest? instance))
            {
                restores.Add(RestoreAsync(resource, client, instance, snapshot, cancellationToken));
            }
        }

        bool[] restored = await Task.WhenAll(restores).ConfigureAwait(false);
        return Array.IndexOf(restored, false) >= 0;
    }

    // Restores a snapshot on one instance. One that cannot be reached, does not restore it, or names
    // no control plane is unregistered: when it next renews it publishes again, and the answer carries
    // the recorded state. That includes a restore the caller cancelled, so no instance stays
    // registered without the write.
    private async Task<bool> RestoreAsync(GatewayResource resource, IApplicationInstanceClient client, ApplicationInstanceManifest instance, ApplicationActionSnapshot snapshot, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        ApplicationActionStatus? refusal = null;
        if (instance.ControlPlaneEndpoint is not null)
        {
            try
            {
                ApplicationActionResult? result = await client.RestoreAsync(instance, snapshot, cancellationToken).ConfigureAwait(false);
                if (result?.Status == ApplicationActionStatus.Succeeded)
                {
                    return true;
                }

                refusal = result?.Status;
                failure = result is null ? new InvalidOperationException(ErrorMessages.InstanceClientResultNull) : null;
            }
            catch (Exception exception)
            {
                // Deliberately broad, cancellation included: whatever kept the restore from the
                // instance, it restores the write when it publishes again.
                failure = exception;
            }
        }

        Drop(resource, instance, failure, refusal);
        return false;
    }

    // Unregisters an instance the gateway could not call. The record changes with the next save.
    private void Drop(GatewayResource resource, ApplicationInstanceManifest instance, Exception? failure, ApplicationActionStatus? refusal)
    {
        resource.Unregister(instance.Key);
        ApplicationGatewayEventSource.Log.InstanceDropped(Name, instance.Provider, instance.Name, instance.Key, failure, refusal);
    }

    private static bool Declares(ApplicationInstanceManifest instance, ActionName action)
    {
        foreach (ApplicationActionDescriptor descriptor in instance.Actions)
        {
            if (descriptor.Name == action)
            {
                return true;
            }
        }

        return false;
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
        var stopped = new List<string>();
        var causes = new List<Exception>();
        var skipped = new List<string>();
        foreach (GatewayStep step in steps)
        {
            GatewayResource resource = Find(step.Resource);
            if (step.IsSkipped)
            {
                skipped.Add(step.Name);
            }
            else if (resource.IsStopped)
            {
                // The provider reported the workload stopping before an instance published.
                Exception? cause = resource.StopCause;
                stopped.Add(cause is null
                    ? Format(ErrorMessages.GatewayResourceEnded, step.Name)
                    : Format(ErrorMessages.GatewayResourceFailedWith, step.Name, cause.GetType().Name, cause.Message));
                if (cause is not null)
                {
                    causes.Add(cause);
                }
            }
            else if (resource.IsClosed)
            {
                missed.Add(step.Name);
            }
        }

        if (stopped.Count > 0)
        {
            throw Stopped(model, stopped, causes, missed, skipped);
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

    // The run's failure when workloads stopped before an instance published: those first, each with
    // its reason, then what else missed its window and what did not deploy. The workloads' own
    // failures are the inner exception, so a caller sees a program's exception as it was thrown.
    private ApplicationGatewayException Stopped(IApplicationModel model, List<string> stopped, List<Exception> causes, List<string> missed, List<string> skipped)
    {
        var message = new StringBuilder(Format(ErrorMessages.GatewayResourcesStopped, model.Name, stopped.Count, string.Join("; ", stopped)));
        if (missed.Count > 0)
        {
            message.Append(' ').Append(Format(ErrorMessages.GatewayResourcesStoppedAlsoMissed, missed.Count, StartupTimeout, string.Join(", ", missed)));
        }

        if (skipped.Count > 0)
        {
            message.Append(' ').Append(Format(ErrorMessages.GatewayResourcesStoppedSkipped, string.Join(", ", skipped)));
        }

        Exception? inner = causes.Count switch
        {
            0 => null,
            1 => causes[0],
            _ => new AggregateException(causes),
        };

        return new ApplicationGatewayException(ApplicationGatewayErrorCode.ResourceFailed, message.ToString(), inner);
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
                    ApplicationGatewayEventSource.Log.ResourceSkipped(Name, step.Resource);
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
        ApplicationGatewayEventSource.Log.ResourcePlanned(Name, manifest, plan.Change);
        if (deployment is { } id)
        {
            ApplicationGatewayEventSource.Log.ResourceDeploying(Name, manifest, id);
            await _infrastructure.DeployResourceAsync(model, manifest, settings, id, cancellationToken).ConfigureAwait(false);
            await SaveAsync(cancellationToken).ConfigureAwait(false);

            if (!await WaitForStartAsync(model, resource, id, cancellationToken).ConfigureAwait(false))
            {
                if (resource.IsStopped)
                {
                    ApplicationGatewayEventSource.Log.ResourceStopped(Name, manifest, resource.StopCause);
                }
                else
                {
                    ApplicationGatewayEventSource.Log.ResourceMissed(Name, manifest, StartupTimeout);
                }

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

        ApplicationGatewayEventSource.Log.ResourceReady(Name, manifest);
        return true;
    }

    // Waits, within the startup window, for an instance of the deployment to publish, while the
    // provider watches the deployment's workload. A workload that stops first closes the window at
    // once and is recorded on the resource with the provider's reason. Returns whether it started.
    private async Task<bool> WaitForStartAsync(IApplicationModel model, GatewayResource resource, DeploymentId deployment, CancellationToken cancellationToken)
    {
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task watch = _infrastructure.WatchResourceAsync(model, resource.Declared, deployment, watching.Token).AsTask();
        try
        {
            Task first = await Task.WhenAny(resource.Registered, watch).WaitAsync(StartupTimeout, cancellationToken).ConfigureAwait(false);
            if (first == resource.Registered)
            {
                return true;
            }

            // The watch ended first: the run was cancelled, or the workload stopped. An instance that
            // registered in the meantime still counts.
            cancellationToken.ThrowIfCancellationRequested();
            if (!resource.TryClose())
            {
                return true;
            }

            resource.MarkStopped(watch.IsFaulted ? watch.Exception!.InnerException : watch.IsCanceled ? new TaskCanceledException(watch) : null);
            return false;
        }
        catch (TimeoutException)
        {
            // The window closed. A resource that registered in the meantime is not missed.
            return !resource.TryClose();
        }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);

            // The watch may end later, or fail after the step stopped caring; observe it without
            // waiting, so a provider slow to honor cancellation never holds the step.
            _ = watch.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
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
            ApplicationGatewayEventSource.Log.ResourceRetired(Name, retired.Provider, retired.Name);
            Volatile.Write(ref _retiredResources, Array.FindAll(Volatile.Read(ref _retiredResources), entry => !ReferenceEquals(entry, retired)));
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Saves never overlap, and each writes the whole record as it stands when the save begins. A
    // save the store refuses means a later run took the record over: this run stops, and whatever
    // asked for the save fails.
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        bool saved;
        await _saves.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            saved = await _store.SaveAsync(CreateRecord(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saves.Release();
        }

        if (!saved)
        {
            throw Supersede();
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

        return new ApplicationGatewayRecord(Name, resources.AsReadOnly()) { Epoch = Volatile.Read(ref _epoch) };
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
