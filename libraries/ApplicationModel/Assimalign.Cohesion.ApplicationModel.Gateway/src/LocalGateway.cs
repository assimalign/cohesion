using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The default gateway for local development. It realizes each <see cref="IExecutableResource"/>
/// as a supervised child process, starting them in dependency order, gating each on readiness,
/// and stopping them in reverse order. It requires no platform tooling.
/// </summary>
public sealed class LocalGateway : ApplicationGateway
{
    private readonly LocalGatewayOptions _options;
    private readonly InMemoryResourceStateManager _state = new();
    private readonly LocalResourceResolver _resolver;
    private readonly IReadOnlyList<IApplicationResourceController> _controllers;
    private readonly LocalGatewayProcessSupervisor _supervisor;

    /// <summary>
    /// Initializes a new <see cref="LocalGateway"/> with default options.
    /// </summary>
    public LocalGateway()
        : this(new LocalGatewayOptions())
    {
    }

    /// <summary>
    /// Initializes a new <see cref="LocalGateway"/> with the given options.
    /// </summary>
    /// <param name="options">The options controlling resolution, readiness, and shutdown.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A string, trust key, or controller registration in <paramref name="options"/> is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A duration, threshold, or retry count in <paramref name="options"/> is outside its supported range.
    /// </exception>
    public LocalGateway(LocalGatewayOptions options)
        : base(options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _resolver = new LocalResourceResolver(_options.BaseDirectory ?? AppContext.BaseDirectory);
        string stateDirectory = _options.StateDirectory
            ?? Path.Combine(Environment.CurrentDirectory, ".cohesion");
        _options.ExportDirectory ??= stateDirectory;
        var ports = new LocalPortStore(stateDirectory);
        var mounts = new LocalMountMaterializer(stateDirectory);
        var processState = new LocalProcessStateStore(stateDirectory);
        var preparer = new LocalResourcePreparer(ports, mounts, _options);
        _supervisor = new LocalGatewayProcessSupervisor(_options, processState);
        _controllers = new IApplicationResourceController[]
        {
            new LocalPlanController(_options, preparer, _supervisor),
        };
    }

    /// <inheritdoc/>
    public override ResourceName Name => "local";

    /// <inheritdoc/>
    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    /// <inheritdoc/>
    protected override IApplicationResourceStateManager State => _state;

    internal IApplicationResourceStateManager ResourceStates => _state;

    /// <inheritdoc/>
    protected override void ValidateResource(
        IApplicationModel model,
        IApplicationResourceDescriptor descriptor,
        ResourcePlan plan)
    {
        base.ValidateResource(model, descriptor, plan);
        if (descriptor.Resource is ContainerResource)
        {
            throw new InvalidOperationException(
                $"Resource '{descriptor.Resource.Name}' is image-only. The Local gateway realizes " +
                "executables; select a container gateway with an IImageRealizer.");
        }
    }

    /// <inheritdoc/>
    protected override Task StartObserverAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken) =>
        _supervisor.InitializeAsync(models, cancellationToken);

    /// <inheritdoc/>
    protected override Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken)
    {
        string path;
        if (resource is LocalExecutableResource localExecutable)
        {
            path = _resolver.ResolveExecutable(localExecutable.Path);
        }
        else if (resource is IManifestResource manifestResource
                 && resource is IExecutableResource)
        {
            string? appHost = manifestResource.Manifest.Artifact.AppHost;
            if (string.IsNullOrWhiteSpace(appHost))
            {
                throw new FileNotFoundException(
                    $"Resource '{resource.Name}' has no apphost in its manifest artifact. "
                    + "The local gateway does not launch the managed assembly DLL.");
            }

            path = _resolver.ResolveAppHost(appHost);
        }
        else if (resource is IExecutableResource)
        {
            throw new InvalidOperationException(
                $"Executable resource '{resource.Name}' has no resource manifest. "
                + "Add a plain or disabled executable with AddExecutable(name, path, options).");
        }
        else
        {
            throw new InvalidOperationException(
                $"The local gateway can only realize executable resources; '{resource.Name}' does not implement IExecutableResource.");
        }

        return Task.FromResult<IResourceArtifact>(new ExecutableArtifact(resource.Id, path));
    }
}
