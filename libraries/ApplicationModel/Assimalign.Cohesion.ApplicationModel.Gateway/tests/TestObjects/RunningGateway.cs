using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// A gateway running a model until disposed, as one deployment of a pipeline runs it.
/// </summary>
internal sealed class RunningGateway : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    private RunningGateway(ApplicationGateway gateway, IApplicationModel model)
    {
        Gateway = gateway;
        _run = gateway.RunAsync(model, _stop.Token);
    }

    public ApplicationGateway Gateway { get; }

    /// <summary>Starts a gateway over the platform and waits until its startup completes.</summary>
    public static async Task<RunningGateway> StartAsync(
        TestInfrastructure infrastructure,
        IGatewayStore store,
        IApplicationModel model,
        TimeSpan? startupTimeout = null,
        TimeSpan? leaseDuration = null)
    {
        ApplicationGateway gateway = ApplicationGateway.Create(infrastructure, store, startupTimeout ?? TimeSpan.FromSeconds(10), leaseDuration);
        infrastructure.Gateway = gateway;

        var running = new RunningGateway(gateway, model);
        await gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        return running;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _run;
        _stop.Dispose();
    }
}
