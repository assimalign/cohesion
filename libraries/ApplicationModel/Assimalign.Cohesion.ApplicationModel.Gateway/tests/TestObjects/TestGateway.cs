using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A concrete <see cref="ApplicationGateway"/> for exercising the generic realization algorithm
/// with fake controllers and a real <see cref="InMemoryResourceStateManager"/>.
/// </summary>
internal sealed class TestGateway : ApplicationGateway
{
    private readonly IApplicationResourceStateManager _state;
    private readonly IReadOnlyList<IApplicationResourceController> _controllers;
    private readonly Func<IApplicationResourceDescriptor, IResourceControlContext, CancellationToken, ValueTask<ResourceInputs>>? _inputResolver;

    public List<string> Gathered { get; } = new();

    public TestGateway(
        IApplicationResourceStateManager state,
        IReadOnlyList<IApplicationResourceController> controllers,
        TimeSpan? readinessBudget = null,
        ApplicationGatewayOptions? options = null,
        Func<IApplicationResourceDescriptor, IResourceControlContext, CancellationToken, ValueTask<ResourceInputs>>? inputResolver = null)
        : base(Configure(options, readinessBudget))
    {
        _state = state;
        _controllers = controllers;
        _inputResolver = inputResolver;
    }

    public override ResourceName Name => "test";

    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    protected override IApplicationResourceStateManager State => _state;

    protected override Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken)
    {
        Gathered.Add(resource.Name.ToString());
        return Task.FromResult<IResourceArtifact>(new TestArtifact(resource.Id));
    }

    protected override ValueTask<ResourceInputs> ResolveInputsAsync(
        IApplicationResourceDescriptor descriptor,
        IResourceControlContext context,
        CancellationToken cancellationToken) =>
        _inputResolver is null
            ? base.ResolveInputsAsync(descriptor, context, cancellationToken)
            : _inputResolver(descriptor, context, cancellationToken);

    private static ApplicationGatewayOptions Configure(
        ApplicationGatewayOptions? options,
        TimeSpan? readinessBudget)
    {
        options ??= new ApplicationGatewayOptions();
        options.ReadinessBudget = readinessBudget ?? TimeSpan.FromSeconds(30);
        return options;
    }

    private sealed class TestArtifact : IResourceArtifact
    {
        public TestArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }
    }
}
