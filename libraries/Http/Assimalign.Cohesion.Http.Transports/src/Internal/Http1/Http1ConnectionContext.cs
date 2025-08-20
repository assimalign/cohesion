using Assimalign.Cohesion.Transports;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Http.Internal;

internal class Http1ConnectionContext : IHttpConnectionContext
{
    public EndPoint LocalEndPoint { get; init; }
    public EndPoint RemoteEndPoint { get; init; }
    public ITransportConnectionPipe Pipe { get; init; }
    public IDictionary<string, object?> Items { get; init; }


    public IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
