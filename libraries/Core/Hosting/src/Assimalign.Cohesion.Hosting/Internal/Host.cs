using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class Host : IHost
{
    private readonly Timer timer;
    private readonly HostContext context;
    private readonly IList<ValueTask> startTask;
    private readonly IList<ValueTask> stopTask;

    private volatile bool stop;
    private volatile bool isStopped;


    public Host(HostContext context)
    {
        this.context = context;
        this.timer = new Timer(new TimerCallback(OnCheckInterval), context, TimeSpan.Zero, context.StateCheckInterval);
        this.startTask= new List<ValueTask>();
        this.stopTask = new List<ValueTask>();
    }

    public HostServerStateCallbackAsync StateCallback => this.context.ServerStateCallback;

    public async ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken == default ? CancellationToken.None : cancellationToken;
        
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        foreach (var server in context.Servers)
        {
            try
            {
                startTask.Add(server.StartAsync(cancellationTokenSource.Token));
            }
            catch (Exception exception) when (!context.ThrowExceptionOnServerStartFailure)
            {
                continue;
            }
            catch (Exception exception)
            {
                throw new BadStartException($"Unable to start server: {server.GetType().Name}", exception);
            }
        }

        // At this point all servers should be started.
        // Begin monitoring server state.
        await MonitorAsync(cancellationTokenSource.Token);
    }    
    private async ValueTask MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (stop || cancellationToken.IsCancellationRequested)
                {
                    foreach (var server in context.Servers)
                    {
                        stopTask.Add(SafeAsyncWrapper(server.StopAsync(cancellationToken)));
                    }

                    isStopped = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var server in context.Servers)
            {
                stopTask.Add(SafeAsyncWrapper(server.StopAsync(cancellationToken)));
            }

            isStopped = true;
        }
    }

    // This method will be used for the TimerCallback
    // to monitor server state. Current interval is 5 seconds
    private void OnCheckInterval(object state)
    {
        if (state is HostContext context)
        {
            foreach (var server in this.context.Servers)
            {
                if (server.State is not null)
                {
                    StateCallback.Invoke(server.State);
                }
            }
        }
    }

    private async ValueTask SafeAsyncWrapper(ValueTask task)
    {
        try
        {
            await task; 
        }
        catch
        {

        }
    }

    public void Dispose()
    {
        DisposeAsync().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        stop = true;

        while (isStopped == false)
        {

        }

        var tasks = new List<Task>()
        {
            timer.DisposeAsync().AsTask()
        };

        foreach (var stop in startTask)
        {
            tasks.Add(stop.AsTask());
        }

        await Task.WhenAll(tasks);
    }
}
