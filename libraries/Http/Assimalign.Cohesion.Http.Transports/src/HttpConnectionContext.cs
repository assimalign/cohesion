using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

public abstract class HttpConnectionContext : TransportConnectionContext, IHttpConnectionContext
{
    public abstract IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default);
}
