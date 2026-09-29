using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// Presents a built model's graph and commands without its provider registrations, the way an
/// application set sees a member imported from its describe output before the member's callback
/// registers anything.
/// </summary>
internal sealed class ProviderlessApplicationModel : IApplicationModel
{
    private readonly IApplicationModel _inner;

    public ProviderlessApplicationModel(IApplicationModel inner)
    {
        _inner = inner;
    }

    public ApplicationName Name => _inner.Name;

    public IApplicationEnvironment Environment => _inner.Environment;

    public GatewayRunMode RunMode => _inner.RunMode;

    public ResourceName GatewayIdentity => _inner.GatewayIdentity;

    public string Owner => _inner.Owner;

    public bool Adopt => _inner.Adopt;

    public bool RestartOrphans => _inner.RestartOrphans;

    public IReadOnlyList<IApplicationResourceDescriptor> Descriptors => _inner.Descriptors;

    public IReadOnlyList<IApplicationResource> Resources => _inner.Resources;

    public IReadOnlyList<ResourceManifest> Manifests => _inner.Manifests;

    public IReadOnlyList<ResourcePlan> Plans => _inner.Plans;

    public IReadOnlyList<IResourceCommand> Commands => _inner.Commands;
}
