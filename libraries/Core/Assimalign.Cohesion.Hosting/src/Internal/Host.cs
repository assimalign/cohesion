using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class Host : IHost
{
    private readonly HostOptions _options;
    private readonly HostContext _context;
    private readonly IEnumerable<Action<IHostContext>> _traces;

    internal Host(HostOptions options)
    {
        _options = options;
        _traces = options.GetTraces();
        _context = new HostContext()
        {
            HostId = HostId.New(),
            Environment = new HostEnvironment()
            {
                Name = options.Environment
            }
        };
        Id = _context.HostId;
    }

    public HostId Id { get; }
    public HostContext Context => _context;
    IHostContext IHost.Context => Context;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Create a cancellation token source to pass
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Let's control the task completion of 'RunAsync()` by manually setting the 
        // results when Cancellation is Requested
        var taskCompletionSource = new TaskCompletionSource<Host>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Set the Shutdown handle
        _context.ShutdownCallback = () =>
        {
            cancellationTokenSource.Cancel();
        };

        // Let's register a callback to complete the task 
        cancellationTokenSource.Token.Register(state =>
        {
            Trace(_context);

            var source = (TaskCompletionSource<Host>)state!;

            source.SetResult(this);

        }, taskCompletionSource);

        _context.State = HostState.Starting;

        // Begin trace
        Trace(_context);

        await StartAsync(cancellationTokenSource.Token).ConfigureAwait(false);

        _context.State = HostState.Running;

        Trace(_context);

        await taskCompletionSource.Task.ConfigureAwait(false);

        _context.State = HostState.Stopping;

        Trace(_context);

        await StopAsync(cancellationTokenSource.Token).ConfigureAwait(false);
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
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
            _context.Shutdown();
        });

        var services = Context.HostedServices;

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
    public async Task StopAsync(CancellationToken cancellationToken = default)
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
    private void Trace(IHostContext context)
    {
        foreach (var trace in _traces)
        {
            trace(context);
        }
    }
}