using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

/// <summary>
/// Realizes enabled, composable project resources by invoking their entry points under
/// isolated ambient contexts in the gateway process.
/// </summary>
public sealed class InProcessGateway : ApplicationGateway
{
    private readonly InMemoryResourceStateManager _state = new();
    private readonly IReadOnlyList<IApplicationResourceController> _controllers;
    private readonly ProcessHost _host;

    /// <summary>Initializes an in-process gateway with default options.</summary>
    public InProcessGateway()
        : this(new InProcessGatewayOptions())
    {
    }

    /// <summary>Initializes an in-process gateway with the supplied options.</summary>
    /// <param name="options">The invocation, probe, restart, and state options.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A directory option is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A duration, liveness threshold, or restart-attempt limit is outside its supported range.
    /// </exception>
    public InProcessGateway(InProcessGatewayOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        string stateDirectory = Path.GetFullPath(
            options.StateDirectory
            ?? Path.Combine(Environment.CurrentDirectory, ".cohesion"));
        Directory.CreateDirectory(stateDirectory);
        options.ExportDirectory ??= stateDirectory;

        ResourceContext outerContext = ResourceRuntime.Current;
        _host = new ProcessHost(outerContext.EnvironmentName, stateDirectory);
        var contexts = new InProcessContextFactory(stateDirectory);
        var probes = new InProcessProbeRunner(options);
        var supervisor = new InProcessMemberSupervisor(
            _host,
            options,
            probes,
            new ResourceEntryInvoker());
        _controllers =
        [
            new InProcessPlanController(contexts, supervisor, outerContext),
        ];
    }

    /// <inheritdoc/>
    public override ResourceName Name => "inprocess";

    /// <inheritdoc/>
    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    /// <inheritdoc/>
    protected override IApplicationResourceStateManager State => _state;

    internal IApplicationResourceStateManager ResourceStates => _state;

    internal ProcessHost Host => _host;

    /// <inheritdoc/>
    protected override Task<IResourceArtifact> GatherAsync(
        IApplicationResource resource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!InProcessResourceBindings.TryGet(resource, out InProcessResourceBinding? binding))
        {
            throw MissingBinding(resource);
        }

        return Task.FromResult<IResourceArtifact>(new InProcessResourceArtifact(
            resource.Id,
            binding.EntryAssembly,
            binding.ContentRootPath));
    }

    /// <inheritdoc/>
    protected override void ValidateResource(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourcePlan plan)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(plan);

        if (descriptor.Resource is IExternalResource)
        {
            return;
        }
        if (descriptor.Resource is not IManifestResource manifestResource)
        {
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' cannot be colocated by gateway '{Name}': "
                + "plain executables are never nested; enable CohesionApplicationModel and use a project reference.");
        }
        if (!manifestResource.Manifest.Artifact.Composable)
        {
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' cannot be colocated by gateway '{Name}' because "
                + "artifact.composable=false.");
        }
        if (!InProcessResourceBindings.TryGet(
            descriptor.Resource,
            out InProcessResourceBinding? binding))
        {
            throw MissingBinding(descriptor.Resource);
        }
        if (!ResourceRuntime.IsEntryRegistered(binding.EntryAssembly))
        {
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' cannot be colocated by gateway '{Name}' because "
                + $"assembly '{binding.EntryAssembly.GetName().Name}' did not register an enabled resource entry point. "
                + "Set CohesionApplicationModel=enabled on the referenced executable project.");
        }
        if (binding.EntryAssembly.EntryPoint is null)
        {
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' cannot be colocated by gateway '{Name}' because "
                + $"assembly '{binding.EntryAssembly.GetName().Name}' has no Program.Main entry point.");
        }
        if (!Directory.Exists(binding.ContentRootPath))
        {
            throw new DirectoryNotFoundException(
                $"Resource '{descriptor.Resource.Name}' in-process content root "
                + $"'{binding.ContentRootPath}' does not exist.");
        }
    }

    /// <inheritdoc/>
    protected override Task StartObserverAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken) =>
        ((Assimalign.Cohesion.Hosting.IHost)_host).StartAsync(cancellationToken);

    /// <inheritdoc/>
    protected override Task StopObserverAsync(CancellationToken cancellationToken) =>
        ((Assimalign.Cohesion.Hosting.IHost)_host).StopAsync(cancellationToken);

    private InvalidOperationException MissingBinding(IApplicationResource resource) =>
        new(
            $"Resource '{resource.Name}' cannot be colocated by gateway '{Name}' because it has no "
            + "generated in-process entry binding. Use an enabled, composable Cohesion project reference; "
            + "plain executables, package-only manifests, and image-only resources are never nested.");
}
