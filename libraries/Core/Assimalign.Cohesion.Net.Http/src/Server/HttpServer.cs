using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Net.Http;

using Internal;
using Transports;

public partial class HttpServer
{
    private readonly IList<ITransport> transports;
    private readonly HttpConnectionFactory factory;

    private readonly HttpServerOptions options;

    internal HttpServer(HttpServerOptionsInternal options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        this.options = options;
        this.transports = options.Transports;
        this.executor = options.Executor;
        this.factory = HttpConnectionFactory.New();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return ProcessAsync(cancellationToken);
    }

    private async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (true)
        {
            await foreach (var transportConnection in ProcessTransportConnectionsAsync().WithCancellation(cancellationTokenSource.Token))
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
                        catch (Exception)
                        {

                        }
                    }, transportConnection, false);
                }
                catch (Exception)
                {
                    continue;
                }
            }
        }

        async IAsyncEnumerable<ITransportConnection> ProcessTransportConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Will use this as
            var taskQueue = new Dictionary<Task<ITransportConnection>, int>();

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

                var tasks = taskQueue.Select(task => task.Key);
                var taskCompleted = await Task.WhenAny(tasks);

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

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var transport in this.transports)
        {
            transport.Dispose();
        }

        return Task.CompletedTask;
    }


    public void Dispose() => DisposeAsync().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}