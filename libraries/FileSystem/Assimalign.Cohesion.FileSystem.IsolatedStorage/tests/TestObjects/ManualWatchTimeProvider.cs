using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.IsolatedStorage.Tests;

/// <summary>
/// Creates manually driven timers so watcher lifetime tests do not depend on elapsed time.
/// </summary>
internal sealed class ManualWatchTimeProvider : TimeProvider
{
    private readonly List<ManualWatchTimer> _timers = new();

    public IReadOnlyList<ManualWatchTimer> Timers
    {
        get
        {
            lock (_timers)
            {
                return _timers.ToArray();
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualWatchTimer(callback, state, dueTime);
        lock (_timers)
        {
            _timers.Add(timer);
        }
        return timer;
    }

    /// <summary>
    /// Models cancellation of scheduled callbacks separately from callbacks already queued.
    /// </summary>
    internal sealed class ManualWatchTimer : ITimer
    {
        private readonly object _gate = new();
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _disposed;
        private bool _scheduled;
        private int _disposeCalls;

        internal ManualWatchTimer(TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _callback = callback;
            _state = state;
            _scheduled = dueTime != Timeout.InfiniteTimeSpan;
        }

        public bool IsDisposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        public int DisposeCalls
        {
            get
            {
                lock (_gate)
                {
                    return _disposeCalls;
                }
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }
                _scheduled = dueTime != Timeout.InfiniteTimeSpan;
                return true;
            }
        }

        /// <summary>
        /// Runs one scheduled tick, returning false when disposal has stopped the timer.
        /// </summary>
        public bool Fire()
        {
            lock (_gate)
            {
                if (_disposed || !_scheduled)
                {
                    return false;
                }
            }

            _callback(_state);
            return true;
        }

        /// <summary>
        /// Delivers a callback queued before disposal, as a real thread-pool timer can do.
        /// </summary>
        public void InvokeQueuedCallback() => _callback(_state);

        public void Dispose()
        {
            lock (_gate)
            {
                _disposeCalls++;
                _disposed = true;
                _scheduled = false;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
