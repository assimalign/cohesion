using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

public abstract class HttpConnection : IHttpConnection
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract IAsyncEnumerable<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default);
    public abstract void Dispose();
}
