using System;
using System.Web;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;


namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;
using System.Net.WebSockets;

internal partial class Http1Connection : HttpConnection
{
    private readonly IHttpContextExecutor executor;
    private readonly ITransportConnection connection;

    public Http1Connection(HttpConnectionContext context)
    {
        this.connection = context.Connection;
        this.executor = context.Executor;
    }

    internal override async IAsyncEnumerable<IHttpContext> ProcessAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        while (true)
        {
            var context = new Http1Context();

            try
            {
              
            }
            catch (Exception exception)
            {
                connection.Abort();
                break;
            }

            yield return context;
        }
    }
}