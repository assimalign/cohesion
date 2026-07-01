using System;
using System.Collections.Generic;
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
    public LocalGateway(LocalGatewayOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = new LocalResourceResolver(_options.BaseDirectory ?? AppContext.BaseDirectory);
        var supervisor = new LocalGatewayProcessSupervisor(_state, _options);
        _controllers = new IApplicationResourceController[] { new LocalProcessController(supervisor) };
    }

    /// <inheritdoc/>
    public override ResourceName Name => "local";

    /// <inheritdoc/>
    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    /// <inheritdoc/>
    protected override IApplicationResourceStateManager State => _state;

    /// <inheritdoc/>
    protected override TimeSpan ReadinessBudget => _options.ReadinessBudget;

    /// <inheritdoc/>
    protected override Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken)
    {
        if (resource is not IExecutableResource executable)
        {
            throw new InvalidOperationException(
                $"The local gateway can only realize executable resources; '{resource.Name}' does not implement IExecutableResource.");
        }

        string path = _resolver.Resolve(executable.Artifact);
        return Task.FromResult<IResourceArtifact>(new ExecutableArtifact(resource.Id, path));
    }
}
