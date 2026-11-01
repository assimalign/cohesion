using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// The test platform's gateway client: it forwards each publication and renewal to whichever
/// gateway the platform currently runs, as a fixed endpoint reaches a restarted gateway, and names
/// the address the test platform's instance client reaches the instance at.
/// </summary>
internal sealed class GatewayForwarder : IApplicationInstanceGatewayClient
{
    private readonly TestInfrastructure _infrastructure;

    public GatewayForwarder(TestInfrastructure infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public PlatformName Platform => "test";

    public ValueTask<ApplicationInstancePublication> PublishAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return _infrastructure.Gateway!.PublishAsync(Address(manifest), cancellationToken);
    }

    public ValueTask<bool> RenewAsync(ApplicationInstanceManifest manifest, CancellationToken cancellationToken = default)
    {
        return _infrastructure.Gateway!.RenewAsync(Address(manifest), cancellationToken);
    }

    // The test platform's instance client finds the instance among the platform's running ones.
    public ValueTask StartAsync(IApplicationInstanceControlPlane controlPlane, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }

    private static ApplicationInstanceManifest Address(ApplicationInstanceManifest manifest)
    {
        return manifest with { ControlPlaneEndpoint = new Uri($"test://instances/{manifest.Key}/") };
    }
}
