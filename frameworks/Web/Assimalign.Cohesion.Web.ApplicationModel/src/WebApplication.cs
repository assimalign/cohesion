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
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web.Internal;

public partial class WebApplication : IWebApplication, IWebApplicationPipelineBuilder, IHostService, IDisposable
{
    public bool _isDisposed;
    public bool _isStarted;

    private readonly IList<ITransport> _transports;
    private readonly List<Func<WebApplicationMiddleware, WebApplicationMiddleware>> _middleware;
    private readonly HttpConnectionFactory _httpConnectionFactory;

    internal WebApplication(WebApplicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _middleware = new List<Func<WebApplicationMiddleware, WebApplicationMiddleware>>();
    }

    private async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pipeline = (this as IWebApplicationPipelineBuilder).Build();

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
                                await pipeline.ExecuteAsync(httpContext, cancellationToken);

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
                foreach (var transport in _transports)
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

    void IDisposable.Dispose()
    {
        throw new NotImplementedException();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IWebApplicationPipeline pipeline = (this as IWebApplicationPipelineBuilder).Build();

        foreach (var transport in _transports)
        {
            bool queued = ThreadPool.UnsafeQueueUserWorkItem<ITransport>(async state =>
            {
                var httpConnectionFactory = new HttpConnectionFactory(new HttpConnectionOptions()
                {

                });

                await foreach (ITransportConnection transportConnection in state.EnumerateAsync())
                {
                    IHttpConnection httpConnection = httpConnectionFactory.Create(transportConnection);

                    await foreach (IHttpContext context in httpConnection.ReceiveAsync())
                    {
                        // Execute application pipeline.
                        await pipeline.ExecuteAsync(context, CancellationToken.None);

                        // Dispose of the HttpContext
                        await foreach (IAsyncDisposable disposable in httpConnection.SendAsync(context))
                        {
                            await disposable.DisposeAsync();
                        }
                    }
                }

            }, transport, true);

            if (!queued)
            {

            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    public WebApplication Use(Func<IHttpContext, WebApplicationMiddleware, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        Func<IHttpContext, WebApplicationMiddleware, Task> middleware2 = middleware;

        return Use((WebApplicationMiddleware next) => (IHttpContext context) =>
        {
            return middleware2.Invoke(context, next);
        });
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="middleware"></param>
    /// <returns></returns>
    public WebApplication Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _middleware.Add(middleware);

        return this;
    }

    IWebApplicationPipeline IWebApplicationPipelineBuilder.Build()
    {
        var middleware = new WebApplicationMiddleware(context =>
        {
            return Task.CompletedTask;
        });

        for (int i = _middleware.Count - 1; i >= 0; i--)
        {
            middleware = _middleware[i].Invoke(middleware);
        }

        return new WebApplicationPipeline(middleware);
    }

    IWebApplicationPipelineBuilder IWebApplicationPipelineBuilder.Use(Func<WebApplicationMiddleware, WebApplicationMiddleware> middleware)
    {
        return Use(middleware);
    }
}
