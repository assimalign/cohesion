using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Hosting;
using Assimalign.Cohesion.Net.Logging;
using Assimalign.Cohesion.Net.Transports;
using Assimalign.Cohesion.Net.Http.Internal;

public sealed partial class HttpServer : IHostServer
{
    private readonly ILogger logger;
    private readonly IList<ITransport> transports;
    private readonly IHttpContextExecutor executor;
    private readonly HttpConnectionFactory factory;

    private readonly HttpServerOptions options;

    internal HttpServer(HttpServerOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        this.options = options;
        this.transports = options.Transports;
        this.executor = options.Executor ?? new HttpContextExecutor();
        this.State = new HttpServerState()
        {
            ServerName = options.ServerName
        };
        this.factory = HttpConnectionFactory.New();
    }
    
    public IHostServerState State { get; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {        
        return ProcessAsync(cancellationToken);
    }
    private async ValueTask ProcessAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await foreach (var transportConnection in ProcessTransportConnectionsAsync().WithCancellation(cancellationToken))
            {
                try
                {
                    // Depending on the HTTP version the ability to taskQueue HTTP workloads will allow 
                    // for the continuation of accepting further requests of other clients. Version such as
                    // HTTP 1.1, HTTP/2 and HTTP/3 which allow for multiplex/pipelining connections (layman: receiving multiple HTTP request over one transportConnection.)
                    // could technically stay open indefinitely. Although HTTP1.1 uses the same transportConnection, it does not implement true multiplexing since it does not have data frames that allow for responding
                    // to multiple HTTP Requests asynchronously
                    var queued = ThreadPool.UnsafeQueueUserWorkItem(async connection =>
                    {
                        try
                        {
                            var httpConnection = factory.Create(new()
                            {
                                Connection = connection,
                                Executor = this.executor,
                            });

                            // Since transportConnection can remain open using IAsyncEnumerable will allow for 
                            // async disposable
                            await foreach (IAsyncDisposable disposable in httpConnection.ProcessAsync().WithCancellation(cancellationToken))
                            {
                                await disposable.DisposeAsync();
                            }
                        }
                        catch (Exception exception)
                        {

                        }
                    }, transportConnection, false);
                }
                catch (Exception exception)
                {
                    continue;
                }
            }
        }

        async IAsyncEnumerable<ITransportConnection> ProcessTransportConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Will use this as
            var taskQueue = new Dictionary<Task<ITransportConnection?>, int>();

            while (true)
            {
                // Queue/Re-Queue
                foreach (var transport in this.transports)
                {
                    var hashCode = transport.GetHashCode();

                    if (!taskQueue.Values.Contains(hashCode))
                    {
                        // The underlying transports should handle exceptions and restart accepting 
                        // connections which is why checking null is all that is needed.
                        taskQueue.Add(transport.InitializeAsync(cancellationToken), hashCode);
                    }
                }

                var tasks           = taskQueue.Select(task => task.Key);
                var taskCompleted   = await Task.WhenAny(tasks);

                taskQueue.Remove(taskCompleted);

                var transportConnection = await taskCompleted;

                // If null, most likely result of connection being aborted.
                if (transportConnection is null)
                {
                    continue;
                }

                yield return transportConnection;
            }
        }
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var transport in this.transports)
        {
            transport.Dispose();
        }

        return ValueTask.CompletedTask;
    }


    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => StopAsync();

    
}
