using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Timeout.Tests.TestObjects;

internal sealed class TrackingTimeProvider : TimeProvider
{
    private readonly List<TrackingTimer> _timers = new();

    public int CreatedTimerCount => _timers.Count;

    public int OutstandingTimerCount => _timers.FindAll(static timer => timer.IsArmed).Count;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        TrackingTimer timer = new(callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    public void FireTimers()
    {
        foreach (TrackingTimer timer in _timers.ToArray())
        {
            timer.Fire();
        }
    }

    private sealed class TrackingTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _disposed;
        private int _armed;

        public TrackingTimer(TimerCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
        }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool IsArmed => !IsDisposed && Volatile.Read(ref _armed) != 0;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (IsDisposed)
            {
                return false;
            }

            Volatile.Write(ref _armed, dueTime == global::System.Threading.Timeout.InfiniteTimeSpan ? 0 : 1);
            return true;
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire()
        {
            if (IsArmed && Interlocked.Exchange(ref _armed, 0) != 0)
            {
                _callback(_state);
            }
        }
    }
}
