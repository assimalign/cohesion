using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// The manifest endpoint an in-process instance publishes to: it forwards to whichever gateway the
/// platform currently runs, as a fixed endpoint reaches a restarted gateway.
/// </summary>
internal sealed class GatewayForwarder : IApplicationInstancePublisher
{
    private readonly TestInfrastructure _infrastructure;

    public GatewayForwarder(TestInfrastructure infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return _infrastructure.Gateway!.PublishAsync(manifest, cancellationToken);
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return _infrastructure.Gateway!.RenewAsync(manifest, cancellationToken);
    }
}
