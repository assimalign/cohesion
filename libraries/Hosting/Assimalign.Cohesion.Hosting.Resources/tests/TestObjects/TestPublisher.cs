using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

/// <summary>
/// Records each publication and answers with the snapshots it was given, as a gateway answers with
/// the state written since the last deployment.
/// </summary>
internal sealed class TestPublisher : IResourceManifestPublisher
{
    private readonly IReadOnlyList<ResourceStateSnapshot> _snapshots;

    public TestPublisher(params ResourceStateSnapshot[] snapshots)
    {
        _snapshots = snapshots;
    }

    public List<ResourceManifest> Published { get; } = [];

    public ValueTask<IReadOnlyList<ResourceStateSnapshot>> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        Published.Add(manifest);
        return ValueTask.FromResult(_snapshots);
    }
}
