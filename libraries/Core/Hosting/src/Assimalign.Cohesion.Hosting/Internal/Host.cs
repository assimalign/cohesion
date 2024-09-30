using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class Host : IHost
{
    private readonly HostOptions options;
    public Host(HostOptions options)
    {
        this.options = options;
        this.Context = new()
        {
            Environment = new HostEnvironment()
            {
                Name = options.Environment
            }
        };
    }

    public HostContext Context { get; }
    IHostContext IHost.Context => Context;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Let's control the task completion of 'RunAsync()` by manually setting the 
        // results when Cancellation is Requested
        var taskCompletionSource = new TaskCompletionSource<Host>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Create a cancellation token source to pass
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Set the Shutdown handle
        Context.ShutdownCallback = () =>
        {
            cancellationTokenSource.Cancel();
        };

        // Let's register a callback to complete the task 
        cancellationTokenSource.Token.Register(state =>
        {
            options.Trace(Context);

            var source = (TaskCompletionSource<Host>)state!;

            source.SetResult(this);

        }, taskCompletionSource);

        Context.State = HostState.Starting;

        // Begin trace
        options.Trace(Context);

        await StartAsync(cancellationTokenSource.Token).ConfigureAwait(false);

        Context.State = HostState.Running;

        options.Trace(Context);

        await taskCompletionSource.Task.ConfigureAwait(false);

        Context.State = HostState.Stopping;

        options.Trace(Context);

        await StopAsync(cancellationTokenSource.Token).ConfigureAwait(false);
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var startCancellationToken = cancellationToken;

        if (options.ServiceStartupTimeout is not null)
        {
            var timeoutCancellationTokenSource = new CancellationTokenSource(options.ServiceStartupTimeout.Value);
            var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellationTokenSource.Token,
                cancellationToken);

            startCancellationToken = linkedCancellationTokenSource.Token;
        }
        startCancellationToken.Register(() =>
        {
            // TODO: Need to change implementation for safer shutdown process
            Context.Shutdown();
        });

        var services = Context.HostedServices;

        if (options.StartServicesConcurrently)
        {
            var tasks = new List<Task>();

            for (int i = 0; i < services.Count; i++)
            {
                var service = services[i];

                if (service is IHostLifecycleService lifecycleService)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await lifecycleService.StartingAsync(startCancellationToken).ConfigureAwait(false);
                        await lifecycleService.StartAsync(startCancellationToken).ConfigureAwait(false);
                        await lifecycleService.StartedAsync(startCancellationToken).ConfigureAwait(false);
                    }));
                }
                else
                {
                    tasks.Add(service.StartAsync(startCancellationToken));
                }
            }

            await Task.WhenAll(tasks);
        }
        else
        {
            for (int i = 0; i < services.Count; i++)
            {
                var service = services[i];

                if (service is IHostLifecycleService lifecycleService)
                {
                    await lifecycleService.StartingAsync(startCancellationToken).ConfigureAwait(false);
                    await lifecycleService.StartAsync(startCancellationToken).ConfigureAwait(false);
                    await lifecycleService.StartedAsync(startCancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await service.StartAsync(startCancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        var shutdownCancellationToken = cancellationToken;

        if (options.ServiceShutdownTimeout is not null)
        {
            var timeoutCancellationTokenSource = new CancellationTokenSource(options.ServiceShutdownTimeout.Value);
            var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellationTokenSource.Token,
                cancellationToken);

            shutdownCancellationToken = linkedCancellationTokenSource.Token;
        }

        var services = Context.HostedServices;

        for (int i = 0; i < services.Count; i++)
        {
            var service = services[i];

            if (service is IHostLifecycleService lifecycleService)
            {
                await lifecycleService.StoppingAsync(shutdownCancellationToken).ConfigureAwait(false);
                await lifecycleService.StopAsync(shutdownCancellationToken).ConfigureAwait(false);
                await lifecycleService.StoppedAsync(shutdownCancellationToken).ConfigureAwait(false);
            }
            else
            {
                await service.StopAsync(shutdownCancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        
    }
}