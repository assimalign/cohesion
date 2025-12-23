using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

public abstract class Host<TContext> : IHost where TContext : HostContext
{
    private readonly HostOptions<TContext> _options;
    private readonly HostEventListener _telemetry;

    private bool _isDisposed;

    protected Host(HostOptions<TContext> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _telemetry = HostEventListener.Create(options.EventListeners);
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
            var source = (TaskCompletionSource<Host<TContext>>)state!;

            source.SetResult(this);

        }, taskCompletionSource);

        await (this as IHost).StartAsync(cancellationTokenSource.Token).ConfigureAwait(false);

        await taskCompletionSource.Task.ConfigureAwait(false);

        await (this as IHost).StopAsync(cancellationTokenSource.Token).ConfigureAwait(false);
    }


    async Task IHost.StartAsync(CancellationToken cancellationToken)
    {
        SetState(HostState.Starting);
        ReportEvent(new HostEvent("Starting", "Debug"));

        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_options.StartupTimeout != Timeout.InfiniteTimeSpan)
        {
            cancellationTokenSource.CancelAfter(_options.StartupTimeout);
        }

        // Ensure we have a shutdown callback registered. This happens when StartAsync is called directly.
        Context.ShutdownCallback ??= async () =>
        {
            await (this as IHost).StopAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        };

        cancellationToken = cancellationTokenSource.Token;
        cancellationToken.Register(() =>
        {
            // TODO: Need to change implementation for safer shutdown process
            Context.Shutdown();
        });

        List<Exception> exceptions = new();
        bool concurrent = _options.StartServicesConcurrently;
        bool abortOnFirstException = !concurrent;

        IEnumerable<IHostService> services = Context.HostedServices;
        IEnumerable<IHostLifecycleService>? lifecycleServices = GetLifecycleServices(services);

        if (lifecycleServices is not null)
        {
            await ForeachService(
                lifecycleServices,
                cancellationToken,
                concurrent,
                abortOnFirstException,
                exceptions,
                (service, token) => service.StartingAsync(token))
                .ConfigureAwait(false);

            ThrowIfError();
        }

        await ForeachService(
            services,
            cancellationToken,
            concurrent,
            abortOnFirstException,
            exceptions,
            (service, token) => service.StartAsync(token))
            .ConfigureAwait(false);

        ThrowIfError();

        if (lifecycleServices is not null)
        {
            await ForeachService(
                lifecycleServices,
                cancellationToken,
                concurrent,
                abortOnFirstException,
                exceptions,
                (service, token) => service.StartedAsync(token))
                .ConfigureAwait(false);

            ThrowIfError();
        }

        SetState(HostState.Running);
        ReportEvent(new HostEvent("Started", "Debug"));

        void ThrowIfError()
        {
            if (exceptions.Count > 0)
            {
                if (exceptions.Count == 1)
                {
                    // Rethrow if it's a single error
                    Exception exception = exceptions[0];
                    ReportEvent(new HostEvent("StartFailure", "Error"), exception);
                    ExceptionDispatchInfo.Capture(exception).Throw();
                }
                else
                {
                    var exception = new AggregateException("One or more hosted services failed to start.", exceptions);
                    ReportEvent(new HostEvent("StartFailure", "Error"), exception);
                    throw exception;
                }
            }
        }
    }
    async Task IHost.StopAsync(CancellationToken cancellationToken)
    {
        SetState(HostState.Stopping);
        ReportEvent(new HostEvent("Stopping", "Debug"));

        InvalidOperationException.ThrowIf(Context.ShutdownCallback is null, "Host has not started.");

        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_options.ShutdownTimeout != Timeout.InfiniteTimeSpan)
        {
            cancellationTokenSource.CancelAfter(_options.ShutdownTimeout);
        }

        cancellationToken = cancellationTokenSource.Token;

        List<Exception> exceptions = new();
        bool concurrent = _options.StartServicesConcurrently;
        bool abortOnFirstException = !concurrent;

        IEnumerable<IHostService> services = Context.HostedServices.Reverse();
        IEnumerable<IHostLifecycleService>? lifecycleServices = GetLifecycleServices(services);

        if (lifecycleServices is not null)
        {
            await ForeachService(
                lifecycleServices,
                cancellationToken,
                concurrent,
                abortOnFirstException,
                exceptions,
                (service, token) => service.StoppingAsync(token))
                .ConfigureAwait(false);
        }

        await ForeachService(
            services,
            cancellationToken,
            concurrent,
            abortOnFirstException,
            exceptions,
            (service, token) => service.StopAsync(token))
            .ConfigureAwait(false);

        if (lifecycleServices is not null)
        {
            await ForeachService(
                lifecycleServices,
                cancellationToken,
                concurrent,
                abortOnFirstException,
                exceptions,
                (service, token) => service.StoppedAsync(token))
                .ConfigureAwait(false);
        }


        SetState(HostState.Stopped);
        ReportEvent(new HostEvent("Stopped", "Debug"));

        if (exceptions.Count > 0)
        {
            if (exceptions.Count == 1)
            {
                // Rethrow if it's a single error
                Exception exception = exceptions[0];
                ReportEvent(new HostEvent("StopFailure", "Error"), exception);
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
            else
            {
                var exception = new AggregateException("One or more hosted services failed to start.", exceptions);
                ReportEvent(new HostEvent("StopFailure", "Error"), exception);
                throw exception;
            }
        }
    }
    void IDisposable.Dispose()
    {
        (this as IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(Host<TContext>));
        await (this as IHost).StopAsync();
        _isDisposed = true;
    }

    private void SetState(HostState state)
    {
        Context.SetState(state);
    }
    private void ReportEvent(HostEvent hostEvent, Exception? exception = null)
    {
        _telemetry.Write(new HostEventArgs(
            Context.HostId,
            Context.State,
            Context.Environment,
            hostEvent,
            exception));
    }
    private static List<IHostLifecycleService>? GetLifecycleServices(IEnumerable<IHostService> services)
    {
        List<IHostLifecycleService>? lifecycleServices = null;

        foreach (var service in services)
        {
            if (service is IHostLifecycleService lifecycleService)
            {
                lifecycleServices ??= new List<IHostLifecycleService>();
                lifecycleServices.Add(lifecycleService);
            }
        }

        return lifecycleServices;
    }
    private static async Task ForeachService<T>(
        IEnumerable<T> services,
        CancellationToken token,
        bool concurrent,
        bool abortOnFirstException,
        List<Exception> exceptions,
        Func<T, CancellationToken, Task> operation)
    {
        if (concurrent)
        {
            // The beginning synchronous portions of the implementations are run serially in registration order for
            // performance since it is common to return Task.Completed as a noop.
            // Any subsequent asynchronous portions are grouped together and run concurrently.
            List<Task>? tasks = null;

            foreach (T service in services)
            {
                Task task;
                try
                {
                    task = operation(service, token);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex); // Log exception from sync method.
                    continue;
                }

                if (task.IsCompleted)
                {
                    if (task.Exception is not null)
                    {
                        exceptions.AddRange(task.Exception.InnerExceptions); // Log exception from async method.
                    }
                    else if (task.IsCanceled)
                    {
                        exceptions.Add(new TaskCanceledException(task));
                    }
                }
                else
                {
                    // The task encountered an await; add it to a list to run concurrently.
                    tasks ??= new();
                    tasks.Add(task);
                }
            }

            if (tasks is not null)
            {
                Task groupedTasks = Task.WhenAll(tasks);

                try
                {
                    await groupedTasks.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (groupedTasks.IsFaulted)
                    {
                        exceptions.AddRange(groupedTasks.Exception.InnerExceptions);
                    }
                    else
                    {
                        exceptions.Add(ex);
                    }
                }
            }
        }
        else
        {
            foreach (T service in services)
            {
                try
                {
                    await operation(service, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    if (abortOnFirstException)
                    {
                        return;
                    }
                }
            }
        }
    }
}
