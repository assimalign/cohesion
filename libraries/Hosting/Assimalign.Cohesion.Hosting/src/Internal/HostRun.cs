using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Internal;

internal sealed class HostRun<TContext> : IHostRun
    where TContext : HostContext
{
    private readonly Host<TContext> _host;
    private readonly HostOptions<TContext> _options;
    private readonly Lock _transitionLock = new();
    private readonly TaskCompletionSource _stopCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHostRunObserver? _observer;
    private Exception? _observerException;
    private Action? _shutdownCallback;
    private Exception? _stopException;
    private int _hasBegunStopping;
    private int _isDrainAborted;
    private int _hasRun;
    private int _hasStopped;

    internal HostRun(Host<TContext> host, HostOptions<TContext> options)
    {
        _host = host;
        _options = options;
    }

    public IHost Host => _host;

    public TimeSpan ShutdownTimeout
    {
        get => _options.ShutdownTimeout;
        set => _options.ShutdownTimeout = value;
    }

    internal bool HasBegunStopping => Volatile.Read(ref _hasBegunStopping) != 0;

    public Task RunAsync(
        IHostRunObserver? observer,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _hasRun, 1, 0) != 0)
        {
            throw new InvalidOperationException("A host run handle can only be executed once.");
        }

        _observer = observer;
        return _host.RunCoreAsync(this, cancellationToken);
    }

    public bool TryShutdown(Action? onAccepted = null)
    {
        if (Volatile.Read(ref _shutdownCallback) is null)
        {
            return false;
        }

        return _host.TryShutdown(this, onAccepted);
    }

    internal void HostStarted(Action? shutdownCallback)
    {
        Volatile.Write(ref _shutdownCallback, shutdownCallback);
    }

    internal void BeginStopping()
    {
        Volatile.Write(ref _hasBegunStopping, 1);
    }

    internal void Started()
    {
        lock (_transitionLock)
        {
            if (_hasBegunStopping != 0 || _hasStopped != 0)
            {
                return;
            }

            if (!TryNotify(static (observer, host) => observer.Started(host)))
            {
                _host.TryShutdown(this, onAccepted: null);
            }
        }
    }

    internal void Stopping()
    {
        lock (_transitionLock)
        {
            Volatile.Write(ref _hasBegunStopping, 1);
            if (_hasStopped == 0)
            {
                TryNotify(static (observer, host) => observer.Stopping(host));
            }
        }
    }

    internal void Stopped()
    {
        lock (_transitionLock)
        {
            if (_hasStopped != 0)
            {
                return;
            }

            Volatile.Write(ref _hasStopped, 1);
            TryNotify(static (observer, host) => observer.Stopped(host));
        }
    }

    internal void DrainAborted()
    {
        lock (_transitionLock)
        {
            if (_hasStopped != 0 || _isDrainAborted != 0)
            {
                return;
            }

            _isDrainAborted = 1;
            TryNotify(static (observer, host) => observer.DrainAborted(host));
        }
    }

    internal void CompleteStop(Exception? exception)
    {
        Volatile.Write(ref _stopException, exception);
        _stopCompletionSource.TrySetResult();
    }

    internal async Task WaitForStopAsync()
    {
        await _stopCompletionSource.Task.ConfigureAwait(false);

        Exception? exception = Volatile.Read(ref _stopException);
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        exception = Volatile.Read(ref _observerException);
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private bool TryNotify(Action<IHostRunObserver, IHost> notification)
    {
        IHostRunObserver? observer = _observer;
        if (observer is null)
        {
            return true;
        }

        try
        {
            notification(observer, _host);
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref _observerException, exception, comparand: null);
            return false;
        }
    }
}
