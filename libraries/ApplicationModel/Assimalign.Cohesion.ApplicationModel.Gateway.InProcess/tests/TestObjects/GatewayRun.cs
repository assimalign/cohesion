using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests;

/// <summary>
/// A gateway running until disposed, as one deployment of a gateway program runs it.
/// </summary>
internal sealed class GatewayRun : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    private GatewayRun(ApplicationGateway gateway)
    {
        Gateway = gateway;
        _run = gateway.RunAsync(_stop.Token);
    }

    public ApplicationGateway Gateway { get; }

    /// <summary>Runs the gateway and waits until its startup completes.</summary>
    public static async Task<GatewayRun> StartAsync(ApplicationGateway gateway)
    {
        var run = new GatewayRun(gateway);
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(15));
        return run;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _run;
        _stop.Dispose();
    }
}
