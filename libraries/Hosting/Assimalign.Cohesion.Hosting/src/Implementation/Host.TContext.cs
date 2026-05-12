using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

/// <summary>
/// Provides an abstract base class for hosting and managing the lifecycle of services within a configurable execution
/// context.
/// </summary>
/// <remarks>
/// The Host<TContext> class coordinates the startup, execution, and shutdown of hosted services,
/// managing their lifecycle events and state transitions. It is intended to be subclassed to implement specific hosting
/// behaviors and to provide a strongly-typed context for hosted services. Thread safety and proper disposal are managed
/// internally. Derived classes should override lifecycle methods to customize startup and shutdown logic as
/// needed.
/// </remarks>
/// <typeparam name="TContext">The type of the host context used by the host. Must derive from HostContext.</typeparam>
public abstract class Host<TContext> : IHost where TContext : HostContext
{
    private readonly HostOptions<TContext> _options;

    // Execution Context Info
    private CancellationTokenSource? _cancellationTokenSource;
    private TaskCompletionSource<Host<TContext>>? _taskCompletionSource;

    // State Flags
    private bool _isDisposed;
    private bool _isInit;


    protected Host(HostOptions<TContext> options)
    {
        // Set Options
        _options = ArgumentNullException.ThrowIfNull<HostOptions<TContext>>(options);
    }

    public HostId Id => Context.HostId;
    public abstract TContext Context { get; }
    IHostContext IHost.Context => Context;


    async Task IHost.StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Check if the host is already running
        if (Context.State.IsAny(HostState.Running!, HostState.Starting!))
        {
            return;
        }

        Init(cancellationToken);

        SetState(HostState.Starting);

        await OnStartingAsync(cancellationToken).ConfigureAwait(false);

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
                (service, token) => service.StartingAsync(token)
            ).ConfigureAwait(false);

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

        SetState(HostState.Started);

        await OnStartedAsync(cancellationToken).ConfigureAwait(false);

        void ThrowIfError()
        {
            if (exceptions.Count > 0)
            {
                if (exceptions.Count == 1)
                {
                    // Rethrow if it's a single error
                    Exception exception = exceptions[0];
                    ExceptionDispatchInfo.Capture(exception).Throw();
                }
                else
                {
                    throw new AggregateException("One or more hosted services failed to start.", exceptions);
                }
            }
        }
    }

    async Task IHost.StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Check 
        if (Context.State.IsAny(HostState.Starting!, HostState.Stopping!, HostState.Stopped!))
        {
            return;
        }

        SetState(HostState.Stopping);

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

        if (exceptions.Count > 0)
        {
            if (exceptions.Count == 1)
            {
                // Rethrow if it's a single error
                Exception exception = exceptions[0];
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
            else
            {
                throw new AggregateException("One or more hosted services failed to start.", exceptions);
            }
        }
    }

    void IDisposable.Dispose()
    {
        (this as IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await DisposeAsync(disposing: true);
        GC.SuppressFinalize(this);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Init(cancellationToken);

        await (this as IHost).StartAsync(_cancellationTokenSource!.Token).ConfigureAwait(false);

        await _taskCompletionSource!.Task.ConfigureAwait(false);

        await (this as IHost).StopAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// A lifecycle method for Host startup process.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual Task OnStartingAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual Task OnStartedAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual Task OnStoppingAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected virtual Task OnStoppedAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource = null;
        _taskCompletionSource = null;
        Context.ShutdownCallback = null;


        return Task.CompletedTask;
    }

    protected virtual async ValueTask DisposeAsync(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                await (this as IHost).StopAsync();
            }

            _isDisposed = true;
        }
    }


    private void Init(CancellationToken cancellationToken)
    {
        if (_isInit)
        {
            return;
        }

        // Create a cancellation token source to pass
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Let's control the task completion of 'RunAsync()` by manually setting the 
        // results when Cancellation is Requested
        _taskCompletionSource = new TaskCompletionSource<Host<TContext>>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Set the Shutdown handle
        Context.ShutdownCallback = () =>
        {
            _cancellationTokenSource.Cancel();
        };

        // Let's register a callback to complete the task 
        _cancellationTokenSource.Token.Register(state =>
        {
            var source = (TaskCompletionSource<Host<TContext>>)state!;

            source.SetResult(this);

        }, _taskCompletionSource);

        _isInit = true;
    }

    

    private void SetState(HostState state)
    {
        Context.SetState(state);
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
