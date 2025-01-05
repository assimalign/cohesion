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
        var reader = Http1RequestReader.Create();
        var writer = Http1ResponseWriter.Create();

        while (true)
        {
            var context = new Http1Context();

            try
            {
                await reader.ReadAsync(context, connection);

                await executor.ExecuteAsync(context);

                await writer.WriteAsync(context, connection);

                // Check if the underlying transport connection needs to be closed
                //var connectionHeader = context.Request.Headers.Connection;

                //if (connectionHeader.HasValue && connectionHeader.Value == "close")
                //{
                //    await connection.AbortAsync();
                //    break;
                //}
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