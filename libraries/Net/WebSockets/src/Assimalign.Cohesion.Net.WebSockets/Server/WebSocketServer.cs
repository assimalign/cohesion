using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Net.WebSockets;


using Assimalign.Cohesion.Net.Hosting;
using Assimalign.Cohesion.Net.Transports;
using Assimalign.Cohesion.Net.WebSockets.Internal;

public sealed class WebSocketServer : IHostServer
{
    private readonly IList<ITransport> transports;

    internal WebSocketServer(WebSocketServerOptions options)
    {
        this.transports = options.Transports;

        this.State = new WebSocketServerState()
        {
            ServerName = options.ServerName
        };
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
                    var stream = transportConnection.Pipe.GetStream();
                    var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions()
                    {
                        IsServer = true,
                    });
                    var queued = ThreadPool.UnsafeQueueUserWorkItem(async socket =>
                    {
                        try
                        {
                            
                        }
                        catch (Exception exception)
                        {

                        }
                    }, socket, false);
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

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
