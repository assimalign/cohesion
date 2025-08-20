
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

using Assimalign.Cohesion.Hosting.Internal;
using Assimalign.Cohesion.Internal;

public abstract class Host<TContext> : IHost where TContext : HostContext
{
    private readonly HostOptions<TContext> _options;
    private readonly IEnumerable<Action<TContext>> _traces;

    private bool _isDisposed;

    protected Host(HostOptions<TContext> options)
    {
        _options = ThrowHelper.ThrowIfNull(options);
        _traces = options.GetTraces();
    }

    public HostId Id => Context.HostId;
    public abstract TContext Context { get; }
    IHostContext IHost.Context => Context;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Create a cancellation token source to pass
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Let's control the task completion of 'RunAsync()` by manually setting the 
        // results when Cancellation is Requested
        var taskCompletionSource = new TaskCompletionSource<Host<TContext>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Set the Shutdown handle
        Context.ShutdownCallback = () =>
        {
            cancellationTokenSource.Cancel();
        };

        // Let's register a callback to complete the task 
        cancellationTokenSource.Token.Register(state =>
        {
            Trace(Context);

            var source = (TaskCompletionSource<Host<TContext>>)state!;

            source.SetResult(this);

        }, taskCompletionSource);

        Context.State = HostState.Starting;

        // Begin trace
        Trace(Context);

        await (this as IHost).StartAsync(cancellationTokenSource.Token).ConfigureAwait(false);

        Context.State = HostState.Running;

        Trace(Context);

        await taskCompletionSource.Task.ConfigureAwait(false);

        Context.State = HostState.Stopping;

        Trace(Context);

        await (this as IHost).StopAsync(cancellationTokenSource.Token).ConfigureAwait(false);
    }
    async Task IHost.StartAsync(CancellationToken cancellationToken)
    {
        var startCancellationToken = cancellationToken;

        if (_options.ServiceStartupTimeout is not null)
        {
            var timeoutCancellationTokenSource = new CancellationTokenSource(_options.ServiceStartupTimeout.Value);
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

        var services = Context.HostedServices.ToList();

        if (_options.StartServicesConcurrently)
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
    async Task IHost.StopAsync(CancellationToken cancellationToken)
    {
        var shutdownCancellationToken = cancellationToken;

        if (_options.ServiceShutdownTimeout is not null)
        {
            var timeoutCancellationTokenSource = new CancellationTokenSource(_options.ServiceShutdownTimeout.Value);
            var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellationTokenSource.Token,
                cancellationToken);

            shutdownCancellationToken = linkedCancellationTokenSource.Token;
        }

        var services = Context.HostedServices.ToList();

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
    void IDisposable.Dispose()
    {
        (this as IAsyncDisposable).DisposeAsync().GetAwaiter().GetResult();
    }
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_isDisposed)
        {
            ThrowHelper.ThrowObjectDisposedException(nameof(Host<TContext>));
        }
        await (this as IHost).StopAsync();
        _isDisposed = true;
    }
    private void Trace(TContext context)
    {
        foreach (var trace in _traces)
        {
            trace(context);
        }
    }
}
