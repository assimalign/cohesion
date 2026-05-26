using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class ServerTransport<TConnection> : ServerTransport where TConnection : TransportConnection
{
    /// <summary>
    /// Accepts or listens for an incoming connection.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the accepted connection.</returns>
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task<TConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default);

    protected sealed override async Task<TransportConnection> InitializeAsync(CancellationToken cancellationToken) => await AcceptOrListenAsync(cancellationToken);
}
