using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// Publishes straight into an application resource, as an in-process gateway would: the
/// registration answer is the state the instance restores. Registrations do not expire.
/// </summary>
internal sealed class ApplicationResourcePublisher : IResourceManifestPublisher
{
    private readonly IApplicationResource _resource;

    public ApplicationResourcePublisher(IApplicationResource resource)
    {
        _resource = resource;
    }

    public ValueTask<ResourcePublication> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new ResourcePublication(_resource.Register(manifest), Lease: null));
    }

    public ValueTask<bool> RenewAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_resource.Instances.Any(instance => instance.Key == manifest.Key));
    }
}
