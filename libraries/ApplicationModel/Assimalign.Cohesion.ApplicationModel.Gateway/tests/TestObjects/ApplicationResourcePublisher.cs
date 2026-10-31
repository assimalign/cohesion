using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A gateway client that publishes straight into an application resource, as an in-process gateway
/// would: the registration answer is the state the instance restores. Registrations do not expire,
/// and the test calls the control planes itself.
/// </summary>
internal sealed class ApplicationResourcePublisher : IApplicationResourceInstanceGatewayClient
{
    private readonly IApplicationGatewayDeployment _resource;

    public ApplicationResourcePublisher(IApplicationGatewayDeployment resource)
    {
        _resource = resource;
    }

    public PlatformName Platform => "test";

    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(new ApplicationInstancePublication(_resource.Register(manifest), Lease: null));
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_resource.Instances.Any(instance => instance.Key == manifest.Key));
    }

    public ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}
