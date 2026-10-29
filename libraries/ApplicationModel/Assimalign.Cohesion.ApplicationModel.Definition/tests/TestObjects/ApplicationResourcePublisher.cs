using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Definition.Tests;

/// <summary>
/// Publishes straight into an application resource, as an in-process gateway would: the
/// registration answer is the state the instance restores. Registrations do not expire.
/// </summary>
internal sealed class ApplicationResourcePublisher : IApplicationInstancePublisher
{
    private readonly IApplicationResourceDeployment _resource;

    public ApplicationResourcePublisher(IApplicationResourceDeployment resource)
    {
        _resource = resource;
    }

    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new ApplicationInstancePublication(_resource.Register(manifest), Lease: null));
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_resource.Instances.Any(instance => instance.Key == manifest.Key));
    }
}
