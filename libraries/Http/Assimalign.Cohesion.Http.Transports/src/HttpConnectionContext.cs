using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

public abstract class HttpConnectionContext : IHttpConnectionContext
{
    public abstract EndPoint LocalEndPoint { get; }
    public abstract EndPoint RemoteEndPoint { get; }
    public abstract ITransportConnectionPipe Pipe { get; }
    public abstract IDictionary<string, object?> Items { get; }
    public abstract IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
