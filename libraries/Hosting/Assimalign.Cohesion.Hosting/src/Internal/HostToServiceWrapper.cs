using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class HostToServiceWrapper : IHostService
{
    private readonly IHost _host;
    private readonly Lock _lock = new();
    private Task? _startCompletionTask;
    private CancellationTokenSource? _stopCancellationSource;
    private List<CancellationTokenRegistration>? _stopCancellationRegistrations;
    private Task? _stopTask;
    private int _generation;

    public HostToServiceWrapper(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public ServiceId Id => (Ulid)_host.Id;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? previousStopCancellationSource;
        List<CancellationTokenRegistration>? previousStopCancellationRegistrations;
        var startCompletionSource = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int generation;

        lock (_lock)
        {
            if (_startCompletionTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("The nested host is still starting.");
            }

            if (_stopTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("The nested host is still stopping.");
            }

            previousStopCancellationSource = _stopCancellationSource;
            previousStopCancellationRegistrations = _stopCancellationRegistrations;
            _stopCancellationSource = null;
            _stopCancellationRegistrations = null;
            _stopTask = null;
            _startCompletionTask = startCompletionSource.Task;
            generation = ++_generation;
        }

        if (previousStopCancellationRegistrations is not null)
        {
            foreach (CancellationTokenRegistration registration in previousStopCancellationRegistrations)
            {
                registration.Dispose();
            }
        }

        previousStopCancellationSource?.Dispose();

        Task startTask;
        try
        {
            startTask = _host.StartAsync(cancellationToken);
        }
        catch
        {
            startCompletionSource.TrySetResult();
            throw;
        }

        lock (_lock)
        {
            if (_generation == generation &&
                ReferenceEquals(_startCompletionTask, startCompletionSource.Task))
            {
                // Replace the invocation-window placeholder with the raw task. Once
                // StartAsync returns successfully, an immediate parent stop can therefore
                // observe IsCompleted directly and remains a fully joined shutdown.
                _startCompletionTask = startTask;
            }
        }

        // Keep the raw child operation alive independently of this caller's bounded wait.
        // Parent readiness cancellation may abandon the wait, but deferred cleanup still
        // needs to know when a cancellation-ignoring child eventually finishes starting.
        _ = SignalStartCompletionAsync(startTask, startCompletionSource);
        await startTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (_host.Context.State is not HostState.Started)
        {
            throw new HostStartupException(
                $"Nested host '{_host.Id}' completed startup without reaching the Started state.");
        }

        // A nested host owns its shutdown request but not a runner invocation. Observe the
        // public context signal and translate it directly into the same serialized stop path
        // the parent uses. The generation check prevents a late observer from an earlier run
        // from stopping a restarted child.
        _ = ObserveShutdownAsync(generation);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return StopAsyncCore(cancellationToken, expectedGeneration: null);
    }

    private async Task StopAsyncCore(
        CancellationToken cancellationToken,
        int? expectedGeneration)
    {
        CancellationTokenSource stopCancellationSource;
        Task? startCompletionTask = null;
        Task stopTask;
        TaskCompletionSource? stopSource = null;
        bool deferCleanup = false;

        lock (_lock)
        {
            if (expectedGeneration is int generation && generation != _generation)
            {
                return;
            }

            if (_stopTask is null)
            {
                _stopCancellationSource = new CancellationTokenSource();
                stopSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = stopSource.Task;
                startCompletionTask = _startCompletionTask;
                deferCleanup = startCompletionTask is { IsCompleted: false };
            }

            stopTask = _stopTask!;
            stopCancellationSource = _stopCancellationSource!;

            if (cancellationToken.CanBeCanceled && !stopTask.IsCompleted)
            {
                _stopCancellationRegistrations ??= new List<CancellationTokenRegistration>();
                _stopCancellationRegistrations.Add(cancellationToken.Register(
                    static state => ((CancellationTokenSource)state!).Cancel(),
                    stopCancellationSource));
            }
        }

        if (stopSource is not null)
        {
            _ = StopHostAsync(
                stopSource,
                stopCancellationSource.Token,
                startCompletionTask,
                deferCleanup);

            if (deferCleanup)
            {
                // The parent startup rollback must not inherit an unbounded child start.
                // The retained stop task completes (and remains joinable) after the child
                // settles and the deferred stop runs.
                _ = ObserveDeferredStopAsync(stopTask);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
        }

        await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveShutdownAsync(int generation)
    {
        try
        {
            await _host.Context.WaitForShutdownAsync().ConfigureAwait(false);
            await StopAsyncCore(CancellationToken.None, generation).ConfigureAwait(false);
        }
        catch
        {
            // The shared stop task retains the failure for the parent's StopAsync call. The
            // observer only translates a void shutdown request and must not leak an unobserved
            // fire-and-forget exception.
        }
    }

    private async Task StopHostAsync(
        TaskCompletionSource stopSource,
        CancellationToken cancellationToken,
        Task? startCompletionTask,
        bool startWasIncomplete)
    {
        try
        {
            Task? immediateStopTask = null;
            if (startWasIncomplete)
            {
                // Preserve direct outer-stop propagation even while startup is in progress.
                // Observe this request independently so it cannot delay the settlement wait
                // and authoritative retry below.
                immediateStopTask = StopBeforeReadinessAsync(cancellationToken);
            }

            if (startCompletionTask is not null)
            {
                try
                {
                    await startCompletionTask.ConfigureAwait(false);
                }
                catch
                {
                    // The StartAsync caller owns the startup failure. Stop still gets its
                    // cleanup attempt after that operation settles.
                }
            }

            if (!startWasIncomplete ||
                _host.Context.State is HostState.Starting or HostState.Started)
            {
                // Once an outer readiness/rollback budget has expired, its cancelled token
                // can no longer clean up an implementation that checks cancellation before
                // changing state. The parent has already stopped waiting, so let the child
                // apply its own shutdown policy to the deferred retry. A child that was ready
                // when stop began still receives the live parent token and tighter budget.
                CancellationToken effectiveCancellationToken =
                    startWasIncomplete && cancellationToken.IsCancellationRequested
                        ? CancellationToken.None
                        : cancellationToken;

                await _host.StopAsync(effectiveCancellationToken).ConfigureAwait(false);
            }

            if (immediateStopTask is not null)
            {
                await immediateStopTask.ConfigureAwait(false);
            }

            stopSource.TrySetResult();
        }
        catch (Exception exception)
        {
            stopSource.TrySetException(exception);
        }
    }

    private async Task StopBeforeReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // This is a best-effort pre-readiness request. A settled non-terminal child
            // still receives the authoritative retry from StopHostAsync.
        }
    }

    private static async Task SignalStartCompletionAsync(
        Task startTask,
        TaskCompletionSource startCompletionSource)
    {
        try
        {
            await startTask.ConfigureAwait(false);
        }
        catch
        {
            // StartAsync observes and reports the original child startup failure. The
            // settlement signal exists only to release a pending cleanup operation.
        }
        finally
        {
            startCompletionSource.TrySetResult();
        }
    }

    private static async Task ObserveDeferredStopAsync(Task stopTask)
    {
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        catch
        {
            // A later explicit StopAsync still observes the retained failure. This observer
            // only prevents an eventual background-cleanup fault from becoming unobserved.
        }
    }
}
