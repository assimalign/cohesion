using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// The way the test platform's instances reach the gateway: while it runs, their publications go to
/// the gateway it was started with. Records when it started and stopped.
/// </summary>
internal sealed class TestTransport : IApplicationGatewayTransport
{
    private readonly TestInfrastructure _infrastructure;
    private int _starts;
    private int _stops;

    public TestTransport(TestInfrastructure infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public int Starts => Volatile.Read(ref _starts);

    public int Stops => Volatile.Read(ref _stops);

    /// <summary>The platform's calls made before the transport started.</summary>
    public string[] EventsAtStart { get; private set; } = [];

    /// <summary>An exception starting throws, as a transport that cannot listen would.</summary>
    public Exception? StartFailure { get; init; }

    public ValueTask StartAsync(IApplicationInstancePublisher gateway, CancellationToken cancellationToken = default)
    {
        if (StartFailure is { } failure)
        {
            throw failure;
        }

        EventsAtStart = _infrastructure.Events();
        _infrastructure.Gateway = gateway;
        Interlocked.Increment(ref _starts);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _infrastructure.Gateway = null;
        Interlocked.Increment(ref _stops);
        return ValueTask.CompletedTask;
    }
}
