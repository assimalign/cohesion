using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

/// <summary>
/// A gateway running until disposed, as one deployment of a gateway program runs it.
/// </summary>
internal sealed class GatewayRun : IAsyncDisposable
{
    private GatewayRun(ApplicationGateway gateway)
    {
        Gateway = gateway;
    }

    public ApplicationGateway Gateway { get; }

    /// <summary>Starts the gateway and waits until its startup completes.</summary>
    public static async Task<GatewayRun> StartAsync(ApplicationGateway gateway)
    {
        await gateway.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return new GatewayRun(gateway);
    }

    /// <summary>Stops the gateway, and reports a failure of its run.</summary>
    public async ValueTask DisposeAsync()
    {
        await Gateway.StopAsync();
        await Gateway.Completion;
    }
}
