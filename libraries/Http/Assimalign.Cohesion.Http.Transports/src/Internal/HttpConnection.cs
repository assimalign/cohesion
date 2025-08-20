using Assimalign.Cohesion.Transports;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Internal;

internal abstract class HttpConnection : IHttpConnection
{

    public ConnectionId Id => throw new NotImplementedException();
    public TransportId TransportId => throw new NotImplementedException();
    public ProtocolType Protocol => ProtocolType.Http;
    public ConnectionState State => throw new NotImplementedException();

    public ValueTask<IHttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    ValueTask<ITransportConnectionContext> ISingleStreamTransportConnection.OpenAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
    public void Abort()
    {
        throw new NotImplementedException();
    }

    public ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 
    /// </summary>
    public virtual void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public abstract ValueTask DisposeAsync();

    public ITransportConnectionContext Open()
    {
        throw new NotImplementedException();
    }
}
