using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

internal sealed class WebApplicationServer : IWebApplicationServer
{
    private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

    private readonly IWebApplicationPipeline _pipeline;
    private readonly IHttpConnectionListener _listener;


    public WebApplicationServer(IWebApplicationPipeline pipeline, IHttpConnectionListener listener)
    {
        _pipeline = pipeline;
        _listener = listener;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThreadPool.UnsafeQueueUserWorkItem(async state =>
        {
            using var cancellationTokenSource = (CancellationTokenSource)state!;

            CancellationToken token = cancellationTokenSource.Token;

            while (!token.IsCancellationRequested)
            {
                IHttpConnection httpConnection = await _listener.AcceptOrListenAsync(token);
                IHttpConnectionContext httpConnectionContext = await httpConnection.OpenAsync(token);

                await foreach (IHttpContext httpContext in httpConnectionContext.ReceiveAsync().WithCancellation(token))
                {
                    // Process the received context
                    await _pipeline.ExecuteAsync(httpContext, token).ConfigureAwait(false);

                    await httpConnectionContext.SendAsync(httpContext).ConfigureAwait(false);

                    await httpContext.DisposeAsync().ConfigureAwait(false);
                }
            }

        }, _cancellationTokenSource);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource.Cancel();

        return Task.CompletedTask;
    }
}
