using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// Publishes straight into an application resource, as an in-process gateway would: the
/// registration answer is the state the instance restores.
/// </summary>
internal sealed class ApplicationResourcePublisher : IResourceManifestPublisher
{
    private readonly IApplicationResource _resource;

    public ApplicationResourcePublisher(IApplicationResource resource)
    {
        _resource = resource;
    }

    public ValueTask<IReadOnlyList<ResourceStateSnapshot>> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_resource.Register(manifest));
    }
}
