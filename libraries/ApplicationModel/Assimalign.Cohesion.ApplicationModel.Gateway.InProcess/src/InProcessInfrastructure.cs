using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;
using Assimalign.Cohesion.ApplicationModel.Hosting;
using Assimalign.Cohesion.ApplicationModel.Hosting.InProcess;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// Runs a gateway's resources inside the gateway's own process: each resource is a program the
/// gateway program references, run on a thread of its own in an environment of its own. It is the
/// gateway half of the in-process platform.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AddProgram"/> binds a resource to its program's entry point. Deploying the resource
/// calls the entry point on a new background thread, in an <see cref="AppEnvironmentScope"/> that
/// holds what a platform hands a workload: the application's and the resource's names, the
/// <c>inprocess</c> platform, the endpoint the instance reaches its gateway at, the loopback address
/// of its <c>http</c> endpoint, and every setting as a configuration variable. The program reads them
/// as it would read its environment, and its host stops when the scope's shutdown is signaled, not on
/// the process's signals, which stay with the gateway program.
/// </para>
/// <para>
/// The infrastructure is also the gateway's transport and its instance client:
/// <see cref="InProcessApplicationBuilderExtensions.UseInProcess"/> sets it as all three. A program
/// opts into the platform with the instance half's <c>UseInProcess()</c> on its resource, so it
/// publishes to <see cref="Endpoint"/>, a channel inside the process that the gateway drains on its
/// own flow while it runs: the gateway handles each publication outside the program's environment,
/// and the program waits for its answer with its own cancellation. Once its restored state is in
/// effect, the program attaches its control plane to the same endpoint, and the gateway calls it
/// there directly, with no listener; the program runs each call in its own environment.
/// </para>
/// <para>
/// A program runs as long as the infrastructure, not the gateway: a gateway that stops leaves its
/// programs running, as a platform's workloads outlive their gateway, and a gateway that runs again
/// over the same infrastructure adopts them. A resource is redeployed when its settings change, and
/// tearing it down stops its program. <see cref="DisposeAsync"/> stops every program, the last
/// deployed first, so a program stops before what it depends on.
/// </para>
/// </remarks>
[DebuggerDisplay("{Endpoint}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class InProcessInfrastructure : IInfrastructureProvider, IApplicationGatewayTransport, IApplicationInstanceClient, IAsyncDisposable
{
    // The program's code is part of the gateway program's own build, so the infrastructure tells no
    // versions apart: a resource is a new definition only when its model changes.
    private const string revision = "1";

    // The endpoint every program serves; its address is the resource's endpoint output.
    private const string endpointName = "http";

    private static readonly IReadOnlyDictionary<OutputName, string> _noOutputs = new Dictionary<OutputName, string>();

    private readonly ConcurrentDictionary<(ProviderName Provider, ResourceName Name), InProcessProgram> _programs = new();
    private readonly ConcurrentDictionary<(ProviderName Provider, ResourceName Name), InProcessWorkload> _workloads = new();
    private readonly ConcurrentDictionary<(ProviderName Provider, ResourceName Name), int> _ports = new();
    private readonly List<InProcessWorkload> _started = [];
    private readonly InProcessChannel _channel;
    private readonly IDisposable _registration;
    private TimeSpan _stopTimeout = TimeSpan.FromSeconds(30);
    private int _isDisposed;

    /// <summary>
    /// Creates the infrastructure with no programs, at a new in-process endpoint.
    /// </summary>
    public InProcessInfrastructure()
    {
        Endpoint = new Uri(string.Concat(InProcessPlatform.Name.Value, "://", Guid.NewGuid().ToString("N"), "/"));
        _channel = new InProcessChannel(Endpoint);
        _registration = InProcessPlatform.RegisterGateway(Endpoint, _channel);
    }

    /// <summary>
    /// Gets the platform the infrastructure deploys onto: <c>inprocess</c>. A resource's extensions
    /// for it apply here, and its programs select their in-process client by it.
    /// </summary>
    public PlatformName Platform => InProcessPlatform.Name;

    /// <summary>
    /// Gets the in-process endpoint the programs reach their gateway at: they publish their manifests
    /// to it and attach their control planes to it.
    /// </summary>
    public Uri Endpoint { get; }

    /// <summary>
    /// Gets or sets how long a stopping program has to drain before it is left behind; thirty
    /// seconds unless set.
    /// </summary>
    /// <remarks>
    /// A program that does not stop in time keeps running on its background thread until the
    /// process exits, as a thread cannot be ended from outside.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan StopTimeout
    {
        get => _stopTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _stopTimeout = value;
        }
    }

    /// <summary>
    /// Binds a resource to the program that runs it: the entry point the gateway program calls for
    /// it, such as <c>Orders.Program.Main</c>.
    /// </summary>
    /// <remarks>
    /// The entry point is called with no arguments: everything a platform tells a workload, the
    /// program reads from its environment. Its task completes when the program ends, as an
    /// asynchronous <c>Main</c> returns when its host has stopped.
    /// </remarks>
    /// <param name="provider">The resource's provider, such as <c>Web</c>.</param>
    /// <param name="name">The resource's name.</param>
    /// <param name="main">The program's entry point.</param>
    /// <returns>This infrastructure.</returns>
    /// <exception cref="ArgumentException"><paramref name="provider"/> or <paramref name="name"/> is <see langword="default"/>, or the resource already has a program.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="main"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The infrastructure is disposed.</exception>
    public InProcessInfrastructure AddProgram(ProviderName provider, ResourceName name, Func<string[], Task> main)
    {
        if (provider.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ProviderNameMissing, nameof(provider));
        }

        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceNameMissing, nameof(name));
        }

        ArgumentNullException.ThrowIfNull(main);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        if (!_programs.TryAdd((provider, name), new InProcessProgram(provider, name, main)))
        {
            throw new ArgumentException(Format(ErrorMessages.ProgramDuplicate, provider, name), nameof(name));
        }

        return this;
    }

    /// <summary>
    /// Stops every program, the last deployed first, then stops receiving and removes the endpoint.
    /// </summary>
    /// <remarks>
    /// A program that does not stop within <see cref="StopTimeout"/> is left on its background
    /// thread, and the rest still stop.
    /// </remarks>
    /// <returns>A task that completes when every program has stopped or been left behind.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        InProcessWorkload[] started;
        lock (_started)
        {
            started = [.. _started];
            _started.Clear();
        }

        for (int i = started.Length - 1; i >= 0; i--)
        {
            try
            {
                await started[i].StopAsync(_stopTimeout).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The program did not stop in time; it ends with the process.
            }
        }

        _workloads.Clear();
        await _channel.StopAsync().ConfigureAwait(false);
        _registration.Dispose();
    }

    IReadOnlyCollection<IApplicationResourceAction> IInfrastructureProvider.GetResourceActions(IApplicationModel model, ApplicationResourceManifest resource)
    {
        return [];
    }

    ValueTask<InfrastructurePlan> IInfrastructureProvider.PlanResourceAsync(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        CancellationToken cancellationToken)
    {
        GetProgram(resource);

        // A program that ended runs no more, so it is started again.
        InfrastructureChange change = !_workloads.TryGetValue((resource.Provider, resource.Name), out InProcessWorkload? workload) || workload.HasExited
            ? InfrastructureChange.Create
            : workload.Runs(settings) ? InfrastructureChange.None : InfrastructureChange.Update;

        return ValueTask.FromResult(new InfrastructurePlan(change, revision));
    }

    async ValueTask IInfrastructureProvider.DeployResourceAsync(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        DeploymentId deployment,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        InProcessProgram program = GetProgram(resource);
        (ProviderName, ResourceName) key = (resource.Provider, resource.Name);

        // One program runs a resource at a time, so an update replaces the running one, which gives
        // up the resource's ports before its replacement listens on them.
        if (_workloads.TryRemove(key, out InProcessWorkload? running))
        {
            await StopAsync(running).ConfigureAwait(false);
        }

        int endpoint = _ports.GetOrAdd(key, static _ => GetFreePort());
        InProcessWorkload workload = InProcessWorkload.Start(program, deployment, CreateVariables(model, resource, settings, deployment, endpoint), settings, endpoint);
        _workloads[key] = workload;
        lock (_started)
        {
            _started.Add(workload);
        }
    }

    async ValueTask IInfrastructureProvider.WatchResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, DeploymentId deployment, CancellationToken cancellationToken)
    {
        // The program's entry point returning or throwing is its whole run, so the gateway hears of
        // it at once: an exception arrives as the program threw it. A deployment whose program no
        // longer runs, because another replaced or a teardown removed it, has ended.
        if (_workloads.TryGetValue((resource.Provider, resource.Name), out InProcessWorkload? workload) && workload.Deployment == deployment)
        {
            await workload.Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    ValueTask<IReadOnlyDictionary<OutputName, string>> IInfrastructureProvider.GetResourceOutputsAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<OutputName, string> outputs = _workloads.TryGetValue((resource.Provider, resource.Name), out InProcessWorkload? workload)
            ? new Dictionary<OutputName, string> { [OutputName.Endpoint] = CreateLoopbackUrl(workload.EndpointPort) }
            : _noOutputs;

        return ValueTask.FromResult(outputs);
    }

    async ValueTask IInfrastructureProvider.TeardownResourceAsync(IApplicationModel model, ApplicationResourceManifest resource, CancellationToken cancellationToken)
    {
        (ProviderName, ResourceName) key = (resource.Provider, resource.Name);
        if (_workloads.TryRemove(key, out InProcessWorkload? workload))
        {
            await StopAsync(workload).ConfigureAwait(false);
        }

        _ports.TryRemove(key, out _);
    }

    ValueTask IApplicationGatewayTransport.StartAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        _channel.Start(gateway);
        return ValueTask.CompletedTask;
    }

    async ValueTask IApplicationGatewayTransport.StopAsync(CancellationToken cancellationToken)
    {
        await _channel.StopAsync().ConfigureAwait(false);
    }

    // The gateway calls a program's control plane where the program attached it. The content crosses
    // as bytes, as it would between processes, and the control plane reads it with its own serializer.
    ValueTask<ApplicationActionResult> IApplicationInstanceClient.ExecuteAsync(ApplicationInstanceManifest instance, ActionName action, ApplicationActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(context);

        IApplicationInstanceControlPlane controlPlane = FindControlPlane(instance);
        return controlPlane.ExecuteAsync(action, ApplicationActionContext.Create(context.Operation, controlPlane.Serializer, context.Input), cancellationToken);
    }

    ValueTask<ApplicationActionResult> IApplicationInstanceClient.RestoreAsync(ApplicationInstanceManifest instance, ApplicationActionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(snapshot);

        return FindControlPlane(instance).RestoreAsync(snapshot, cancellationToken);
    }

    // A program attaches its control plane once its restored state is in effect and detaches it when
    // it stops; one that is not attached gives no answer, so the gateway drops it until it publishes
    // again.
    private IApplicationInstanceControlPlane FindControlPlane(ApplicationInstanceManifest instance)
    {
        return _channel.Find(instance.Key)
            ?? throw new InvalidOperationException(Format(ErrorMessages.InstanceNotAttached, instance.Key, instance.Provider, instance.Name, Endpoint.AbsoluteUri));
    }

    // What a platform hands a workload, as its environment: who it is, the deployment that started
    // it, its platform and where its gateway is, where it listens, and its settings.
    private Dictionary<string, string> CreateVariables(
        IApplicationModel model,
        ApplicationResourceManifest resource,
        IReadOnlyDictionary<SettingName, string> settings,
        DeploymentId deployment,
        int endpoint)
    {
        // The contract's application is the model the gateway runs, and its gateway variable names
        // the platform, so the program selects its in-process client.
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppEnvironment.Variables.Application] = model.Name.Value,
            [AppEnvironment.Variables.Gateway] = Platform.Value,
            [AppEnvironment.Variables.Resource] = resource.Name.Value,
            [AppEnvironment.Variables.ManifestEndpoint] = Endpoint.AbsoluteUri,
            [AppEnvironment.Variables.Deployment] = deployment.ToString(),
            [AppEnvironment.Variables.Endpoint(endpointName, "HOST")] = IPAddress.Loopback.ToString(),
            [AppEnvironment.Variables.Endpoint(endpointName, "PORT")] = endpoint.ToString(CultureInfo.InvariantCulture),
            [AppEnvironment.Variables.Endpoint(endpointName, "SCHEME")] = Uri.UriSchemeHttp,
        };

        // The gateway program's environment name is its programs' too.
        if (AppEnvironment.GetValue(AppEnvironment.Variables.Environment) is { } environment)
        {
            variables[AppEnvironment.Variables.Environment] = environment;
        }

        foreach (KeyValuePair<SettingName, string> setting in settings)
        {
            variables[AppEnvironment.Variables.Configuration(setting.Key.Value)] = setting.Value;
        }

        return variables;
    }

    private InProcessProgram GetProgram(ApplicationResourceManifest resource)
    {
        return _programs.TryGetValue((resource.Provider, resource.Name), out InProcessProgram? program)
            ? program
            : throw new InvalidOperationException(Format(ErrorMessages.ProgramMissing, resource.Provider, resource.Name));
    }

    private async Task StopAsync(InProcessWorkload workload)
    {
        lock (_started)
        {
            _started.Remove(workload);
        }

        await workload.StopAsync(_stopTimeout).ConfigureAwait(false);
    }

    private static string CreateLoopbackUrl(int port)
    {
        return string.Concat("http://", IPAddress.Loopback.ToString(), ":", port.ToString(CultureInfo.InvariantCulture));
    }

    // The port is free when it is read; the program binds it a moment later, as a platform's
    // workload binds the port its platform assigned.
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly InProcessInfrastructure _infrastructure;

        public DebugView(InProcessInfrastructure infrastructure)
        {
            _infrastructure = infrastructure;
        }

        public PlatformName Platform => _infrastructure.Platform;

        public Uri Endpoint => _infrastructure.Endpoint;

        public TimeSpan StopTimeout => _infrastructure.StopTimeout;
    }
}
