using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Internal;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;

internal class WebApplicationServer : IWebApplicationServer, IHostService
{
    private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

    private readonly IWebApplicationPipeline _pipeline;
    private readonly IHttpConnectionListener _listener;


    public WebApplicationServer(IWebApplicationPipeline pipeline, IHttpConnectionListener listener)
    {
        _pipeline = pipeline;
        _listener = listener;
    }

    public ServiceId Id { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ThreadPool.UnsafeQueueUserWorkItem(async state =>
        {
            using var cancellationTokenSource = (CancellationTokenSource)state!;

            CancellationToken token = cancellationTokenSource.Token;

            while (!token.IsCancellationRequested)
            {
                var httpConnection = await _listener.AcceptOrListenAsync(token);
                var httpConnectionContext = await httpConnection.OpenAsync(token);

                await foreach (var httpContext in httpConnectionContext.ReceiveAsync().WithCancellation(token))
                {
                    // Process the received context
                    await _pipeline.InvokeAsync(httpContext, token);

                    await foreach (var disposable in httpConnectionContext.SendAsync(httpContext).WithCancellation(token))
                    {
                        await disposable.DisposeAsync();
                    }
                }
            }

        }, _cancellationTokenSource);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource.Cancel();

        return Task.CompletedTask;
    }
}
