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

        cancellationToken = cancellationTokenSource.Token;

        // A cancelled start (caller token or StartupTimeout) signals shutdown so a parked
        // RunAsync can unwind. Init has always run by this point, so the shutdown callback
        // is set; the registration dies with the startup token source.
        cancellationToken.Register(() =>
        {
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

        await OnStoppingAsync(cancellationToken).ConfigureAwait(false);

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

        // Unpark a RunAsync that is waiting on the run signal (the direct StopAsync path),
        // then reset run-state here in the coordinator - deliberately not in the
        // OnStoppedAsync hook, so a subclass override that forgets to call base cannot
        // wedge a later restart.
        _taskCompletionSource?.TrySetResult(this);

        Reset();

        await OnStoppedAsync(cancellationToken).ConfigureAwait(false);

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
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        Init(cancellationToken);

        // Capture this run's state locally: a direct StopAsync resets the fields while
        // this method is parked on the run signal.
        CancellationTokenSource runTokenSource = _cancellationTokenSource!;
        TaskCompletionSource<Host<TContext>> runCompletionSource = _taskCompletionSource!;

        await (this as IHost).StartAsync(runTokenSource.Token).ConfigureAwait(false);

        await runCompletionSource.Task.ConfigureAwait(false);

        // Stop with a fresh token: the run token is cancelled by definition at this point
        // (its cancellation IS the shutdown signal), so passing it would pre-cancel the
        // graceful drain. The stop budget comes from ShutdownTimeout inside StopAsync.
        await (this as IHost).StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// A lifecycle hook invoked after the host enters <see cref="HostState.Starting"/> and
    /// before any hosted service is started.
    /// </summary>
    /// <param name="cancellationToken">Aborts the startup if signaled.</param>
    /// <returns>A task that completes when the hook has finished.</returns>
    protected virtual Task OnStartingAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// A lifecycle hook invoked after every hosted service has started and the host has
    /// entered <see cref="HostState.Started"/>.
    /// </summary>
    /// <param name="cancellationToken">Aborts the startup if signaled.</param>
    /// <returns>A task that completes when the hook has finished.</returns>
    protected virtual Task OnStartedAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// A lifecycle hook invoked after the host enters <see cref="HostState.Stopping"/> and
    /// before any hosted service is stopped. Use it to begin draining work (connection
    /// drain, flush, checkpoint) ahead of service shutdown.
    /// </summary>
    /// <param name="cancellationToken">The shutdown budget; signaled when <see cref="HostOptions{TContext}.ShutdownTimeout"/> expires.</param>
    /// <returns>A task that completes when the hook has finished.</returns>
    protected virtual Task OnStoppingAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// A lifecycle hook invoked after every hosted service has stopped and the host has
    /// entered <see cref="HostState.Stopped"/>. Run-state has already been reset by the
    /// coordinator, so overrides need no base call for the host to restart cleanly.
    /// </summary>
    /// <param name="cancellationToken">The shutdown budget; signaled when <see cref="HostOptions{TContext}.ShutdownTimeout"/> expires.</param>
    /// <returns>A task that completes when the hook has finished.</returns>
    protected virtual Task OnStoppedAsync(CancellationToken cancellationToken = default)
    {
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

        // Per-run state: the run token's cancellation IS the shutdown signal, and the
        // completion source is the run signal RunAsync parks on.
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var taskCompletionSource = new TaskCompletionSource<Host<TContext>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _cancellationTokenSource = cancellationTokenSource;
        _taskCompletionSource = taskCompletionSource;

        // The callback captures this run's source: the coordinator disposes it on stop,
        // and a shutdown signal arriving after that is a no-op, not a fault.
        Context.ShutdownCallback = () =>
        {
            try
            {
                cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The host already stopped and reset this run's state.
            }
        };

        // Complete the run signal on shutdown. TrySetResult: the coordinator also
        // completes the signal when StopAsync is called directly.
        cancellationTokenSource.Token.Register(() =>
        {
            taskCompletionSource.TrySetResult(this);
        });

        _isInit = true;
    }

    /// <summary>
    /// Coordinator-owned reset of per-run state so the host can be started again after a
    /// clean stop. Not part of <see cref="OnStoppedAsync"/> by design: an overridable hook
    /// a subclass may forget to base-call must not own restart correctness.
    /// </summary>
    private void Reset()
    {
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        _taskCompletionSource = null;
        Context.ShutdownCallback = null;
        _isInit = false;
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
