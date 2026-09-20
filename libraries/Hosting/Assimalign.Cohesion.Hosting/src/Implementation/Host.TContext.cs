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
public abstract class Host<TContext> : IHost, IHostRunDispatcher where TContext : HostContext
{
    private readonly HostOptions<TContext> _options;

    // Execution Context Info
    private CancellationTokenSource? _cancellationTokenSource;
    private TaskCompletionSource<Host<TContext>>? _taskCompletionSource;
    private HostRun<TContext>? _hostRun;

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


    Task IHost.StartAsync(CancellationToken cancellationToken)
    {
        return StartAsyncCore(cancellationToken);
    }

    private async Task StartAsyncCore(CancellationToken callerCancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Check if the host is already running
        if (Context.State.IsAny(HostState.Running!, HostState.Starting!))
        {
            return;
        }

        Init(callerCancellationToken);

        CancellationTokenSource runCancellationTokenSource = _cancellationTokenSource!;
        Action shutdownCallback = () =>
        {
            try
            {
                runCancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The host already stopped and reset this run's state.
            }
        };

        // Publish the shutdown callback and the Starting state atomically. Once callers can
        // observe Starting, Shutdown must be accepted for this run and must not be erased by
        // a later state transition.
        Context.BeginStart(shutdownCallback);

        using var startupTimeoutSource = new CancellationTokenSource();

        if (_options.StartupTimeout != Timeout.InfiniteTimeSpan)
        {
            startupTimeoutSource.CancelAfter(_options.StartupTimeout);
        }

        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            runCancellationTokenSource.Token,
            startupTimeoutSource.Token);

        CancellationToken cancellationToken = cancellationTokenSource.Token;

        // A cancelled start (caller token or StartupTimeout) signals shutdown so a parked
        // RunAsync can unwind. Init has always run by this point, so the shutdown callback
        // is set; the registration dies with the startup token source.
        using CancellationTokenRegistration startupCancellationRegistration =
            cancellationToken.Register(Context.Shutdown);

        bool serviceStartupEntered = false;
        try
        {
            await OnStartingAsync(cancellationToken).ConfigureAwait(false);
            serviceStartupEntered = true;

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

            // A service is allowed to ignore its cancellation token. Re-check the combined
            // startup token before publishing readiness so an expired budget can never become
            // a transient or successful Started transition.
            cancellationToken.ThrowIfCancellationRequested();

            Volatile.Read(ref _hostRun)?.HostStarted(shutdownCallback);
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
        catch (Exception exception)
        {
            // Capture cancellation provenance before rollback. Compensation can legitimately
            // run past StartupTimeout; that later timer edge must not relabel an unrelated
            // cancellation that originally failed startup.
            bool startupTimedOut = exception is OperationCanceledException &&
                !callerCancellationToken.IsCancellationRequested &&
                startupTimeoutSource.IsCancellationRequested &&
                _options.StartupTimeout != Timeout.InfiniteTimeSpan;

            // A failed or cancelled start must not wedge the host in Starting with
            // partially-started services leaked: compensate, mark Failed, rethrow.
            await RollbackStartAsync(serviceStartupEntered).ConfigureAwait(false);

            if (startupTimedOut)
            {
                throw new HostStartupException(
                    $"Host '{Id}' exceeded its startup readiness budget of {_options.StartupTimeout}.",
                    exception);
            }

            throw;
        }
    }

    async Task IHost.StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Check: a never-started (Idle) host has nothing to stop - disposing one must be a
        // no-op - and a failed start has already been rolled back, so stopping a Failed
        // host is a clean no-op rather than a second teardown. Starting/Stopping/Stopped
        // guard re-entrancy. Only a Started host proceeds, which also guarantees the
        // per-run state from Init exists.
        HostRun<TContext>? hostRun = Volatile.Read(ref _hostRun);
        Action? beginRunStop = hostRun is null ? null : hostRun.BeginStopping;
        if (!Context.TryBeginStop(beginRunStop))
        {
            return;
        }

        hostRun?.Stopping();

        // A direct StopAsync wakes its parked RunAsync immediately. The run then joins this
        // HostRun's stop-completion signal so it observes the whole drain and the same failure
        // instead of racing ahead of the direct caller.
        if (hostRun is not null)
        {
            _taskCompletionSource?.TrySetResult(this);
        }

        Exception? stopException = null;
        try
        {
            await StopCoreAsync(hostRun, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopException = exception;
            throw;
        }
        finally
        {
            if (hostRun is not null)
            {
                hostRun.CompleteStop(stopException);
            }
        }
    }

    private async Task StopCoreAsync(
        HostRun<TContext>? hostRun,
        CancellationToken cancellationToken)
    {
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_options.ShutdownTimeout != Timeout.InfiniteTimeSpan)
        {
            cancellationTokenSource.CancelAfter(_options.ShutdownTimeout);
        }

        cancellationToken = cancellationTokenSource.Token;

        using CancellationTokenRegistration drainCancellationRegistration =
            hostRun is null
                ? default
                : cancellationToken.Register(
                    static state => ((HostRun<TContext>)state!).DrainAborted(),
                    hostRun);

        await OnStoppingAsync(cancellationToken).ConfigureAwait(false);

        List<Exception> exceptions = new();
        bool concurrent = _options.StopServicesConcurrently;

        // Shutdown is best-effort: a failing service must not leak the ones behind it, so
        // the abort policy is not coupled to the concurrency flag. Failures are collected
        // and thrown together after every service has been given its stop.
        const bool abortOnFirstException = false;

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

        if (cancellationToken.IsCancellationRequested)
        {
            hostRun?.DrainAborted();
        }

        SetState(HostState.Stopped);

        // Ensure an ordinary RunAsync that is waiting on the run signal is unparked, then
        // reset run-state here in the coordinator - deliberately not in the OnStoppedAsync
        // hook, so a subclass override that forgets to call base cannot wedge a later restart.
        _taskCompletionSource?.TrySetResult(this);

        Reset();

        await OnStoppedAsync(cancellationToken).ConfigureAwait(false);
        hostRun?.Stopped();

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
                throw new AggregateException("One or more hosted services failed to stop.", exceptions);
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

    /// <summary>
    /// Starts the host, waits for a shutdown request, and then drains and stops its services.
    /// </summary>
    /// <param name="cancellationToken">Signals a shutdown request for this run.</param>
    /// <returns>A task that represents the complete host run.</returns>
    /// <exception cref="ObjectDisposedException">The host has already been disposed.</exception>
    /// <remarks>
    /// This compatibility member and the <see cref="IHost"/> extension share the same run
    /// coordinator. When <see cref="HostContext.Runner"/> is set, both routes delegate the
    /// complete lifetime to that pipeline.
    /// A token already cancelled at entry starts the host with an uncancelled startup token,
    /// then immediately drains it with a fresh stop budget. Startup failures still propagate.
    /// </remarks>
    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return RunHostAsync(cancellationToken);
    }

    Task IHostRunDispatcher.RunAsync(CancellationToken cancellationToken)
    {
        return RunHostAsync(cancellationToken);
    }

    private Task RunHostAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var run = new HostRun<TContext>(this, _options);
        if (Interlocked.CompareExchange(ref _hostRun, run, comparand: null) is not null)
        {
            return Task.FromException(new InvalidOperationException("A host run is already active."));
        }

        IHostRunner? runner = Context.Runner;
        return RunWithRunnerAsync(run, runner, cancellationToken);
    }

    internal async Task RunCoreAsync(
        HostRun<TContext> hostRun,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!ReferenceEquals(Volatile.Read(ref _hostRun), hostRun))
        {
            throw new InvalidOperationException("The host run handle is no longer active.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // StartAsyncCore owns Init and arms this run's shutdown callback. Passing None
            // prevents the linked run signal from being born cancelled before startup.
            await StartAsyncCore(CancellationToken.None).ConfigureAwait(false);
            hostRun.Started();
            await StopRunAsync(hostRun).ConfigureAwait(false);
            return;
        }

        Init(cancellationToken);

        // Capture this run's completion signal locally: a direct StopAsync resets the
        // fields while this method is parked on the run signal.
        TaskCompletionSource<Host<TContext>> runCompletionSource = _taskCompletionSource!;

        // Preserve the runner's token as cancellation provenance. The per-run token also
        // carries Context.Shutdown, but a startup timeout must not turn that internal signal
        // into an apparent caller cancellation.
        await StartAsyncCore(cancellationToken).ConfigureAwait(false);

        hostRun.Started();

        await runCompletionSource.Task.ConfigureAwait(false);

        await StopRunAsync(hostRun).ConfigureAwait(false);
    }

    // Stop with a fresh budget and join only an accepted stop: a stop that could not begin
    // has no completion to await.
    private async Task StopRunAsync(HostRun<TContext> hostRun)
    {
        if (!hostRun.HasBegunStopping)
        {
            await (this as IHost).StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (hostRun.HasBegunStopping)
        {
            await hostRun.WaitForStopAsync().ConfigureAwait(false);
        }
    }

    internal bool TryShutdown(HostRun<TContext> hostRun, Action? onAccepted)
    {
        return Context.TryShutdown(
            () => ReferenceEquals(Volatile.Read(ref _hostRun), hostRun),
            onAccepted);
    }

    private async Task RunWithRunnerAsync(
        HostRun<TContext> hostRun,
        IHostRunner? runner,
        CancellationToken cancellationToken)
    {
        try
        {
            if (runner is null)
            {
                await hostRun.RunAsync(observer: null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await runner.RunAsync(hostRun, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ClearRun(hostRun);
        }
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

        // Complete the run signal on shutdown. TrySetResult: the coordinator also
        // completes the signal when StopAsync is called directly.
        cancellationTokenSource.Token.Register(() =>
        {
            taskCompletionSource.TrySetResult(this);
        });

        _isInit = true;
    }

    /// <summary>
    /// Best-effort compensation for a failed or cancelled start: stops whatever managed to
    /// start (newest first, without the lifecycle stop ceremony), unparks a waiting
    /// <c>RunAsync</c>, resets run-state, and marks the host <see cref="HostState.Failed"/>.
    /// Rollback failures are swallowed so they never mask the original fault, which the
    /// caller rethrows.
    /// </summary>
    private async Task RollbackStartAsync(bool serviceStartupEntered)
    {
        using var cancellationTokenSource = new CancellationTokenSource(_options.ShutdownTimeout);

        // A rejected OnStarting hook has not entered any service lifecycle. In
        // particular, a terminal host guard must not stop last run's services again.
        if (serviceStartupEntered)
        {
            foreach (IHostService service in Context.HostedServices.Reverse())
            {
                try
                {
                    await service.StopAsync(cancellationTokenSource.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort teardown on the failure path; the original start fault is
                    // what the caller must observe.
                }
            }
        }

        _taskCompletionSource?.TrySetResult(this);

        Reset();

        SetState(HostState.Failed);
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
        Context.ClearShutdownCallback();
        _isInit = false;
    }

    private void ClearRun(HostRun<TContext> hostRun)
    {
        Interlocked.CompareExchange(ref _hostRun, value: null, hostRun);
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
