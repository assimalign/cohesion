using Assimalign.Cohesion.Net.Http.Internal;
using Assimalign.Cohesion.Net.Transports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

//public abstract class HttpConnection
//{
//    /// By using IAsyncEnumerable the benefits will be helpful when implementing HTTP/2 and HTTP/3
//    /// public abstract IAsyncEnumerable<HttpContext> ProcessAsync(CancellationToken cancellationToken = default);
//    public abstract IAsyncEnumerable<IHttpContext> ProcessAsync(CancellationToken cancellationToken = default);

//    /// <summary>
//    /// Responsible for receiving the incoming data from the <see cref="ITransportConnectionPipe"/>. 
//    /// </summary>
//    /// <param name="cancellationToken"></param>
//    /// <returns></returns>
//    protected virtual IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
//    {
//        throw new NotImplementedException();
//    }

//    /// <summary>
//    /// Responsible for sending the outgoing data from the <see cref="ITransportConnectionPipe"/>.
//    /// </summary>
//    /// <param name="context"></param>
//    /// <param name="cancellationToken"></param>
//    /// <returns></returns>
//    protected virtual IAsyncEnumerable<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
//    {
//        throw new NotImplementedException();
//    }

//    /// <summary>
//    /// 
//    /// </summary>
//    /// <param name="transportConnection"></param>
//    /// <returns></returns>
//    public static HttpConnection Create(ITransportConnection transportConnection)
//    {
        
//        return default;
//    }
//}



public abstract class HttpConnection : IHttpConnection
{
    private IHttpConnection? wrappedConnection;

    //public async Task ProcessAsync(CancellationToken cancellationToken = default)
    //{
    //    await foreach (var received in ReceiveAsync().WithCancellation(cancellationToken))
    //    {
    //        // TODO: add execution code

    //        await foreach (var sent in SendAsync(received).WithCancellation(cancellationToken))
    //        {
    //            await sent.DisposeAsync();
    //        }
    //    }
    //}

    protected virtual Task OnReceiveAsync()
    {
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        return wrappedConnection!.ReceiveAsync(cancellationToken);
    }

    public IAsyncEnumerable<IHttpContext> SendAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }



    public static IHttpConnection Create(ITransportConnection transportConnection)
    {
        return default;
    }
}