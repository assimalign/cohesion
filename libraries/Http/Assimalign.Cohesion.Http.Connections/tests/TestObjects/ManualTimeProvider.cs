using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock moves only when the test calls <see cref="Advance"/>, firing
/// every timer that falls due — including the timers of a <see cref="CancellationTokenSource"/> created on
/// it — so a deadline is exercised deterministically. One tick is one <see cref="TimeSpan"/> tick.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private long _now;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);

        lock (_gate)
        {
            _timers.Add(timer);
            timer.ChangeLocked(dueTime, period);
        }

        return timer;
    }

    /// <summary>
    /// Moves the clock forward and fires every timer that falls due, outside the clock's lock.
    /// </summary>
    /// <param name="delta">How far to move the clock.</param>
    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due = new();

        lock (_gate)
        {
            _now += delta.Ticks;

            foreach (ManualTimer timer in _timers)
            {
                if (timer.DueAt is { } dueAt && dueAt <= _now)
                {
                    timer.DueAt = timer.Period is { } period ? _now + period : null;
                    due.Add(timer);
                }
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    /// <summary>
    /// Waits until at least <paramref name="count"/> timers are armed, which is how a test knows the code
    /// under test has begun the wait it means to time out.
    /// </summary>
    /// <param name="count">The number of armed timers to wait for.</param>
    /// <returns>A task that completes once that many timers are armed.</returns>
    public async Task WaitForTimersAsync(int count)
    {
        while (true)
        {
            lock (_gate)
            {
                int armed = 0;

                foreach (ManualTimer timer in _timers)
                {
                    if (timer.DueAt is not null)
                    {
                        armed++;
                    }
                }

                if (armed >= count)
                {
                    return;
                }
            }

            await Task.Delay(1);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        // Guarded by the owner's lock.
        public long? DueAt { get; set; }

        public long? Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                ChangeLocked(dueTime, period);
            }

            return true;
        }

        public void ChangeLocked(TimeSpan dueTime, TimeSpan period)
        {
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime.Ticks;
            Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.Ticks;
        }

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate)
            {
                DueAt = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
