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

    private RunningGateway(ApplicationGateway gateway)
    {
        Gateway = gateway;
        _run = gateway.RunAsync(_stop.Token);
    }

    public ApplicationGateway Gateway { get; }

    /// <summary>
    /// Creates a gateway over the platform with a model built elsewhere, as its builder would, without
    /// running it.
    /// </summary>
    public static ApplicationGateway Create(
        TestInfrastructure infrastructure,
        IApplicationGatewayStore store,
        IApplicationModel model,
        TimeSpan? startupTimeout = null,
        TimeSpan? leaseDuration = null,
        IApplicationParentGateway? parent = null)
    {
        var gateway = new ApplicationGateway(
            model,
            infrastructure,
            store,
            parent,
            [],
            infrastructure.Client,
            startupTimeout ?? TimeSpan.FromSeconds(10),
            leaseDuration ?? TimeSpan.FromSeconds(30));
        infrastructure.Gateway = gateway;
        return gateway;
    }

    /// <summary>Starts a gateway over the platform and waits until its startup completes.</summary>
    public static async Task<RunningGateway> StartAsync(
        TestInfrastructure infrastructure,
        IApplicationGatewayStore store,
        IApplicationModel model,
        TimeSpan? startupTimeout = null,
        TimeSpan? leaseDuration = null,
        IApplicationParentGateway? parent = null)
    {
        var running = new RunningGateway(Create(infrastructure, store, model, startupTimeout, leaseDuration, parent));
        await running.Gateway.Started.WaitAsync(TimeSpan.FromSeconds(10));
        return running;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _run;
        _stop.Dispose();
    }
}
