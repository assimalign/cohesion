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
    private readonly TimeSpan _readinessBudget;

    public TestGateway(
        IApplicationResourceStateManager state,
        IReadOnlyList<IApplicationResourceController> controllers,
        TimeSpan? readinessBudget = null)
    {
        _state = state;
        _controllers = controllers;
        _readinessBudget = readinessBudget ?? TimeSpan.FromSeconds(30);
    }

    public override ResourceName Name => "test";

    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    protected override IApplicationResourceStateManager State => _state;

    protected override TimeSpan ReadinessBudget => _readinessBudget;

    protected override Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken)
        => Task.FromResult<IResourceArtifact>(new TestArtifact(resource.Id));

    private sealed class TestArtifact : IResourceArtifact
    {
        public TestArtifact(ResourceId resource)
        {
            Resource = resource;
        }

        public ResourceId Resource { get; }
    }
}
