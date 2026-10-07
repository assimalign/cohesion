using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// A connection factory whose dial never completes: it signals that the dial started, then waits
/// for the caller's cancellation, so a test can cancel a connect mid-dial.
/// </summary>
internal sealed class StalledConnectionFactory : IConnectionFactory
{
    private readonly TaskCompletionSource _dialing = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConnectionCapabilities Capabilities { get; } = new(
        ConnectionProtocol.Tcp,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: false,
        ConnectionSecurity.None);

    /// <summary>Gets a task that completes when the dial starts.</summary>
    public Task Dialing => _dialing.Task;

    public async ValueTask<IConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        _dialing.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException("The stalled dial ended without a cancellation.");
    }
}
