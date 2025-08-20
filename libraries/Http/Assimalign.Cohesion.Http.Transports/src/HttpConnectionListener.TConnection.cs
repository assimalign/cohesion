using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

using Transports;

public abstract class HttpConnectionListener<TConnection> : ServerTransport<TConnection>, IHttpConnectionListener
    where TConnection : IHttpConnection
{
    internal HttpConnectionListener()
    {

    }

    public sealed override TransportProtocol Protocol { get; } = TransportProtocol.Http;
    async Task<IHttpConnection> IHttpConnectionListener.AcceptOrListenAsync(CancellationToken cancellationToken)
    {
        return await AcceptOrListenAsync(cancellationToken);
    }
}