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
    private TaskCompletionSource? _stoppedSource;

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

    internal Action? ShutdownCallback { get; set; }

    /// <summary>
    /// Returns a task that completes when the host's run ends - a transition to
    /// <see cref="HostState.Stopped"/> or <see cref="HostState.Failed"/> - or a completed
    /// task when it already has. The signal resets on a later start, so each run produces
    /// a fresh signal.
    /// </summary>
    internal Task WhenStoppedAsync()
    {
        lock (_lock)
        {
            if (IsTerminal(_state))
            {
                return Task.CompletedTask;
            }

            _stoppedSource ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            return _stoppedSource.Task;
        }
    }

    internal void SetState(HostState state)
    {
        TaskCompletionSource? stoppedSource = null;

        lock (_lock)
        {
            _state = state;

            if (IsTerminal(state) && _stoppedSource is not null)
            {
                stoppedSource = _stoppedSource;
                _stoppedSource = null;
            }
        }

        // Complete outside the lock so awaiter continuations never run under it.
        stoppedSource?.TrySetResult();
    }

    private static bool IsTerminal(HostState state)
    {
        return state is HostState.Stopped or HostState.Failed;
    }

    public void Shutdown()
    {
        InvalidOperationException.ThrowIf(ShutdownCallback is null, "Host has not started.");

        ShutdownCallback.Invoke();
    }
}
