using System.Collections.Generic;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// An <see cref="IApplicationModel"/> that implements only the required members, so the
/// interface's default members (such as <see cref="IApplicationModel.Providers"/>) apply.
/// </summary>
internal sealed class MinimalApplicationModel : IApplicationModel
{
    public ApplicationName Name => "appa";

    public IApplicationEnvironment Environment => Application.CreateBuilder().Environment;

    public GatewayRunMode RunMode => GatewayRunMode.Run;

    public ResourceName GatewayIdentity => "fake";

    public string Owner => "appa@fake";

    public bool Adopt => false;

    public bool RestartOrphans => false;

    public IReadOnlyList<IApplicationResourceDescriptor> Descriptors => [];

    public IReadOnlyList<IApplicationResource> Resources => [];

    public IReadOnlyList<ResourceManifest> Manifests => [];

    public IReadOnlyList<ResourcePlan> Plans => [];
}
