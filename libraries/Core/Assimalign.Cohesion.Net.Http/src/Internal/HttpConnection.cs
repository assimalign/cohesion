using System;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;

// This base class will be responsible for pumping 
internal abstract class HttpConnection
{
    /// By using IAsyncEnumerable the benefits will be helpful when implementing HTTP/2 and HTTP/3
    /// public abstract IAsyncEnumerable<HttpContext> ProcessAsync(CancellationToken cancellationToken = default);
    internal abstract IAsyncEnumerable<IHttpContext> ProcessAsync([EnumeratorCancellation] CancellationToken cancellationToken = default);

    /// <summary>
    /// Responsible for receiving the incoming data from the <see cref="ITransportConnectionPipe"/>. 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Responsible for sending the outgoing data from the <see cref="ITransportConnectionPipe"/>.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual IAsyncEnumerable<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}