using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.Internal;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// A gateway: it deploys what an application's model declares through an infrastructure provider,
/// receives the manifests its resources' instances publish, and keeps each resource's state until
/// the next deployment.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RunAsync"/> deploys the model's <see cref="IApplicationModel.Deployment"/>: each child
/// gateway, then each resource. Every resource must have an instance publish its manifest within
/// <see cref="StartupTimeout"/>; one that does not is torn down, and the run fails with
/// <see cref="ApplicationGatewayErrorCode.StartupTimedOut"/>. After startup the gateway runs until
/// cancelled. Cancellation stops the gateway; it does not tear down what the gateway deployed.
/// </para>
/// <para>
/// The gateway is transport-neutral: a transport hands each publication to
/// <see cref="PublishAsync"/>, and an in-process resource can publish to the gateway directly,
/// because it is an <see cref="IResourceManifestPublisher"/>. A root gateway and a composite one
/// deploy the same way; the model's <see cref="IApplicationModel.Role"/> says which this is.
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

    private readonly IInfrastructureProvider _infrastructure;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FrozenDictionary<(ProviderName Provider, ResourceName Name), GatewayResource>? _resources;
    private ReadOnlyCollection<IApplicationResource> _aggregates = ReadOnlyCollection<IApplicationResource>.Empty;
    private IApplicationModel? _model;
    private int _lifecycle;

    private ApplicationGateway(IInfrastructureProvider infrastructure, TimeSpan startupTimeout)
    {
        _infrastructure = infrastructure;
        StartupTimeout = startupTimeout;
    }

    /// <summary>
    /// Gets how long every deployed resource has to publish its first manifest.
    /// </summary>
    public TimeSpan StartupTimeout { get; }

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
    /// Gets a task that completes when every deployed resource has published, faults when startup
    /// fails, and is canceled when the run is cancelled before startup completes.
    /// </summary>
    public Task Started => _started.Task;

    /// <summary>
    /// Creates a gateway that deploys through the specified infrastructure provider.
    /// </summary>
    /// <param name="infrastructure">The infrastructure the gateway deploys onto.</param>
    /// <param name="startupTimeout">
    /// How long every deployed resource has to publish its first manifest; five minutes when
    /// <see langword="null"/>.
    /// </param>
    /// <returns>The gateway.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="infrastructure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startupTimeout"/> is not positive.</exception>
    public static ApplicationGateway Create(IInfrastructureProvider infrastructure, TimeSpan? startupTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);

        TimeSpan timeout = startupTimeout ?? _defaultStartupTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(startupTimeout));

        return new ApplicationGateway(infrastructure, timeout);
    }

    /// <summary>
    /// Deploys the model and runs the gateway until cancellation.
    /// </summary>
    /// <param name="model">The model of what the gateway deploys.</param>
    /// <param name="cancellationToken">Signals that the gateway should stop.</param>
    /// <returns>A task that completes when the gateway stops.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The gateway has already run, or the infrastructure provider returned no action list.
    /// </exception>
    /// <exception cref="ApplicationGatewayException">
    /// A resource published no manifest within <see cref="StartupTimeout"/>; it was torn down.
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
            CreateResources(model);

            ApplicationManifest deployment = model.Deployment;
            foreach (ApplicationManifest gateway in deployment.Gateways)
            {
                await _infrastructure.DeployGatewayAsync(model, gateway.Name, cancellationToken).ConfigureAwait(false);
            }

            foreach (ApplicationResourceManifest resource in deployment.Resources)
            {
                await _infrastructure.DeployResourceAsync(model, resource, cancellationToken).ConfigureAwait(false);
            }

            await AwaitStartupAsync(model, cancellationToken).ConfigureAwait(false);
            _started.TrySetResult();

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
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
    /// Registers a published instance manifest with its resource, and returns the state the
    /// instance must restore before it serves.
    /// </summary>
    /// <param name="manifest">The manifest the instance published.</param>
    /// <param name="cancellationToken">Unused; registration completes synchronously.</param>
    /// <returns>The snapshots the instance must restore.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The manifest has no instance key or no action list.</exception>
    /// <exception cref="ApplicationGatewayException">
    /// The gateway is not running (<see cref="ApplicationGatewayErrorCode.PublicationsClosed"/>), does
    /// not run the resource (<see cref="ApplicationGatewayErrorCode.ResourceUnknown"/>), or tore it
    /// down (<see cref="ApplicationGatewayErrorCode.ResourceTornDown"/>).
    /// </exception>
    public ValueTask<IReadOnlyList<ResourceStateSnapshot>> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        FrozenDictionary<(ProviderName Provider, ResourceName Name), GatewayResource>? resources = Volatile.Read(ref _resources);
        if (resources is null || Volatile.Read(ref _lifecycle) != running)
        {
            throw new ApplicationGatewayException(ApplicationGatewayErrorCode.PublicationsClosed, ErrorMessages.GatewayPublicationsClosed);
        }

        GatewayName gateway = Model!.Name;
        if (!resources.TryGetValue((manifest.Provider, manifest.Name), out GatewayResource? resource))
        {
            throw new ApplicationGatewayException(
                ApplicationGatewayErrorCode.ResourceUnknown,
                Format(ErrorMessages.GatewayResourceUnknown, gateway, manifest.Provider, manifest.Name));
        }

        if (resource.IsTornDown)
        {
            throw TornDown(gateway, manifest);
        }

        IReadOnlyList<ResourceStateSnapshot> restore = resource.Aggregate.Register(manifest);
        if (!resource.TryMarkRegistered())
        {
            // The startup window closed between the check above and the registration.
            resource.Aggregate.Unregister(manifest.Key);
            throw TornDown(gateway, manifest);
        }

        return ValueTask.FromResult(restore);
    }

    private void CreateResources(IApplicationModel model)
    {
        var lookup = new Dictionary<(ProviderName Provider, ResourceName Name), GatewayResource>();
        var aggregates = new List<IApplicationResource>();
        foreach (ApplicationResourceManifest resource in model.Deployment.Resources)
        {
            IReadOnlyCollection<IApplicationResourceAction> actions = _infrastructure.GetResourceActions(model, resource)
                ?? throw new InvalidOperationException(Format(ErrorMessages.GatewayResourceActionsNull, resource.Provider, resource.Name));

            ApplicationResource aggregate = ApplicationResource.Create(resource, state: null, actions);
            lookup.Add((resource.Provider, resource.Name), new GatewayResource(aggregate));
            aggregates.Add(aggregate);
        }

        Volatile.Write(ref _model, model);
        Volatile.Write(ref _aggregates, aggregates.AsReadOnly());

        // Publications are accepted from here, before any resource is deployed.
        Volatile.Write(ref _resources, lookup.ToFrozenDictionary());
    }

    private async Task AwaitStartupAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(StartupTimeout);

        var missed = new List<ApplicationResourceManifest>();
        foreach (GatewayResource resource in Volatile.Read(ref _resources)!.Values)
        {
            try
            {
                await resource.Registered.WaitAsync(window.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The window closed. A resource that registered in the meantime is not missed.
                if (resource.TryTearDown())
                {
                    missed.Add(resource.Aggregate.Manifest);
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
            await _infrastructure.TeardownResourceAsync(model, missed[index], cancellationToken).ConfigureAwait(false);
            names[index] = string.Concat(missed[index].Provider.ToString(), "/", missed[index].Name.ToString());
        }

        throw new ApplicationGatewayException(
            ApplicationGatewayErrorCode.StartupTimedOut,
            Format(ErrorMessages.GatewayStartupTimedOut, model.Name, missed.Count, StartupTimeout, string.Join(", ", names)));
    }

    private static ApplicationGatewayException TornDown(GatewayName gateway, ResourceManifest manifest)
    {
        return new ApplicationGatewayException(
            ApplicationGatewayErrorCode.ResourceTornDown,
            Format(ErrorMessages.GatewayResourceTornDown, gateway, manifest.Provider, manifest.Name));
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
