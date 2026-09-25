using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting;

public abstract class HostContext : IHostContext
{
    private readonly Lock _lock;
    private readonly HostId _hostId;
    private HostState _state;
    private TaskCompletionSource? _shutdownSource;
    private Action? _shutdownCallback;
    private bool _isShutdownRequested;

    protected HostContext()
    {
        _lock = new Lock();
        _hostId = HostId.New();
        _state = HostState.Idle;
    }

    public HostId HostId => _hostId;
    public HostState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }
    public abstract IHostEnvironment Environment { get; }
    public abstract IEnumerable<IHostService> HostedServices { get; }

    /// <summary>
    /// Gets or sets the optional pipeline that wraps complete runs of this host.
    /// </summary>
    /// <remarks>
    /// The runner is captured when the host's <c>RunAsync</c> extension begins. Setting this
    /// property does not affect a run that is already active.
    /// </remarks>
    public IHostRunner? Runner { get; set; }

    /// <summary>
    /// Returns a task that completes when shutdown is requested or the host begins stopping,
    /// stops, or fails. The signal resets on a later start, so each run produces a fresh signal.
    /// </summary>
    /// <param name="cancellationToken">Cancels this caller's wait without stopping the host.</param>
    /// <returns>A task that represents the shutdown wait.</returns>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
    {
        Task shutdownTask;

        lock (_lock)
        {
            if (_isShutdownRequested || IsStoppingOrTerminal(_state))
            {
                return Task.CompletedTask;
            }

            _shutdownSource ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shutdownTask = _shutdownSource.Task;
        }

        return cancellationToken.CanBeCanceled
            ? shutdownTask.WaitAsync(cancellationToken)
            : shutdownTask;
    }

    internal void SetState(HostState state)
    {
        TaskCompletionSource? shutdownSource = null;

        lock (_lock)
        {
            _state = state;

            if (state is HostState.Starting && _shutdownCallback is null)
            {
                _isShutdownRequested = false;
            }
            else if (IsStoppingOrTerminal(state))
            {
                _isShutdownRequested = true;
                shutdownSource = _shutdownSource;
                _shutdownSource = null;
            }
        }

        // Complete outside the lock so awaiter continuations never run under it.
        shutdownSource?.TrySetResult();
    }

    internal void BeginStart(Action shutdownCallback)
    {
        ArgumentNullException.ThrowIfNull(shutdownCallback);

        lock (_lock)
        {
            _shutdownCallback = shutdownCallback;
            _isShutdownRequested = false;
            _state = HostState.Starting;
        }
    }

    internal void ClearShutdownCallback()
    {
        lock (_lock)
        {
            _shutdownCallback = null;
        }
    }

    internal bool TryBeginStop(Action? onTransition = null)
    {
        TaskCompletionSource? shutdownSource;

        lock (_lock)
        {
            if (_state is not HostState.Started)
            {
                return false;
            }

            onTransition?.Invoke();
            _state = HostState.Stopping;
            _isShutdownRequested = true;
            shutdownSource = _shutdownSource;
            _shutdownSource = null;
        }

        shutdownSource?.TrySetResult();
        return true;
    }

    private static bool IsStoppingOrTerminal(HostState state)
    {
        return state is HostState.Stopping or HostState.Stopped or HostState.Failed;
    }

    public void Shutdown()
    {
        Action shutdownCallback;
        TaskCompletionSource? shutdownSource;

        lock (_lock)
        {
            InvalidOperationException.ThrowIf(_shutdownCallback is null, "Host has not started.");

            shutdownCallback = _shutdownCallback;
            _isShutdownRequested = true;
            shutdownSource = _shutdownSource;
            _shutdownSource = null;
        }

        shutdownSource?.TrySetResult();
        shutdownCallback.Invoke();
    }

    internal bool TryShutdown(
        Func<bool> isCurrentRun,
        Action? onAccepted = null)
    {
        Action? shutdownCallback;
        TaskCompletionSource? shutdownSource;

        lock (_lock)
        {
            if (_state is not HostState.Started ||
                !isCurrentRun.Invoke() ||
                _shutdownCallback is null)
            {
                return false;
            }

            shutdownCallback = _shutdownCallback;
            onAccepted?.Invoke();
            _isShutdownRequested = true;
            shutdownSource = _shutdownSource;
            _shutdownSource = null;
        }

        shutdownSource?.TrySetResult();
        shutdownCallback.Invoke();
        return true;
    }
}
