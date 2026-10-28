using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// The manifest endpoint an in-process instance publishes to: it forwards to whichever gateway the
/// platform currently runs, as a fixed endpoint reaches a restarted gateway.
/// </summary>
internal sealed class GatewayForwarder : IResourceManifestPublisher
{
    private readonly TestInfrastructure _infrastructure;

    public GatewayForwarder(TestInfrastructure infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public ValueTask<ResourcePublication> PublishAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        return _infrastructure.Gateway!.PublishAsync(manifest, cancellationToken);
    }

    public ValueTask<bool> RenewAsync(ResourceManifest manifest, CancellationToken cancellationToken = default)
    {
        return _infrastructure.Gateway!.RenewAsync(manifest, cancellationToken);
    }
}
