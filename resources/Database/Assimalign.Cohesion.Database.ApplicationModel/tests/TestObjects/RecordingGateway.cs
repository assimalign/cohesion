using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;

namespace Assimalign.Cohesion.Database.ApplicationModel.Tests;

/// <summary>
/// A concrete <see cref="ApplicationGateway"/> over a <see cref="RecordingController"/>
/// and a <see cref="RecordingStateManager"/>, for exercising the generic realization
/// algorithm (dependency-ordered provisioning, teardown, readiness gating, and
/// Failed-dependency blocking) against a real <see cref="DatabaseResource"/>.
/// </summary>
internal sealed class RecordingGateway : ApplicationGateway
{
    private readonly IReadOnlyList<IApplicationResourceController> _controllers;

    public RecordingGateway(RecordingStateManager state, RecordingController controller)
    {
        State = state;
        _controllers = new[] { controller };
    }

    public override ResourceName Name => "recording";

    protected override IReadOnlyList<IApplicationResourceController> Controllers => _controllers;

    protected override IApplicationResourceStateManager State { get; }

    protected override TimeSpan ReadinessBudget => TimeSpan.FromSeconds(5);

    protected override Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken)
        => Task.FromResult<IResourceArtifact>(new RecordingArtifact(resource.Id));

    private sealed class RecordingArtifact : IResourceArtifact
    {
        public RecordingArtifact(ResourceId resource) => Resource = resource;

        public ResourceId Resource { get; }
    }
}
