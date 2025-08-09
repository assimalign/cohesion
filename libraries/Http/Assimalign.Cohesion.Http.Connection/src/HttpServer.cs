
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports;

public class HttpServer : IHttpServer
{
    private readonly HttpListenerOptions _options;

    private List<IHttpConnectionLHandler> _listeners = new List<IHttpConnectionLHandler>();

    public HttpServer(HttpListenerOptions options)
    {
        _options = ThrowHelper.ThrowIfNull(options);
    }

    public async Task RunAsync()
    {
        var transports = _options.Transports;

        if (transports is null || transports.Count == 0)
        {
            ThrowHelper.ThrowInvalidOperationException("No transports are configured for the HTTP server.");
        }

        foreach (var listener in _listeners)
        {
            var isQueued = ThreadPool.UnsafeQueueUserWorkItem(async state =>
            {
                while (true)
                {
                    var connection = await listener.AcceptAsync();

                    await foreach (var context in connection.ReceiveAsync())
                    {
                        //
                        await foreach (IAsyncDisposable disposable in connection.SendAsync(context))
                        {
                            await disposable.DisposeAsync();
                        }
                    }
                }
            },
            listener);
        }

        await foreach (ITransportConnection transportConnection in ProcessTransportConnectionsAsync())
        {
            try
            {
                // Create a new HTTP connection for the transport connection
                var httpConnection = await _options.ConnectionFactory.CreateAsync(transportConnection, _options.CancellationToken);
                
                // Start processing the HTTP connection
                await httpConnection.ProcessAsync(_options.CancellationToken);
            }
            catch (Exception ex)
            {
                // Handle exceptions that occur during connection processing
                _options.Logger.LogError(ex, "An error occurred while processing the transport connection.");
            }
        }
    }


    private async IAsyncEnumerable<ITransportConnection> ProcessTransportConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var transports = _options.Transports;
        var transportConnectionQueue = new Dictionary<TransportId, Task<ITransportConnection>>();

        while (true)
        {
            // Queue/Re-Queue
            foreach (var transport in transports)
            {
                if (!transportConnectionQueue.ContainsKey(transport.Id))
                {
                    transportConnectionQueue.Add(transport.Id, transport.InitializeAsync());
                }
            }

            var taskCompleted = await Task.WhenAny(transportConnectionQueue.Values);

            var transportConnection = await taskCompleted;

            // If null, most likely result of connection being aborted.
            if (transportConnection is null)
            {
                // Since Connection was returned null we need to do a brute force removal of the task from the queue
                // by reference.
                var taskToRemove = transportConnectionQueue.FirstOrDefault(keyValuePair =>
                {
                    var key = keyValuePair.Key;
                    var value = keyValuePair.Value;

                    if (ReferenceEquals(value, taskCompleted))
                    {
                        return true;
                    }

                    return false;
                });

                transportConnectionQueue.Remove(taskToRemove.Key);

                continue;
            }

            transportConnectionQueue.Remove(transportConnection.TransportId);

            yield return transportConnection;
        }
    }
}