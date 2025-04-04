using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Transports;

public sealed class WebApplication : IWebApplication
{
    private readonly IList<ITransport> _transports;
    private readonly IWebApplicationPipeline _pipeline;
    private readonly HttpConnectionFactory _httpConnectionFactory;

    public WebApplication(WebApplicationOptions options)
    {
        ThrowHelper.ThrowIfNull(options);

        _pipeline = options.Pipeline;
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
                            var httpConnection = _httpConnectionFactory.Create(connection);

                            // Since transportConnection can remain open using IAsyncEnumerable will allow for 
                            // async disposable
                            await foreach (IHttpContext httpContext in httpConnection.ReceiveAsync().WithCancellation(cancellationToken))
                            {
                                // TODO: Need to revisit. This may cause a bodle neck depending on
                                // Will probably need to me to a thread to prevent bl
                                await _pipeline.ExecuteAsync(httpContext, cancellationToken);

                                await foreach (IHttpContext disposable in httpConnection.SendAsync(httpContext).WithCancellation(cancellationToken))
                                {
                                    await disposable.DisposeAsync();
                                }
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
                foreach (var transport in this._transports)
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

    #region Implementation

    void IDisposable.Dispose()
    {
        throw new NotImplementedException();
    }

    Task IWebApplication.StartAsync(CancellationToken cancellationToken)
    {
        return ProcessAsync(cancellationToken);
    }

    Task IWebApplication.StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion
}
