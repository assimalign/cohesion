using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Tests.TestObjects;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _utcNow.UtcTicks;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    internal void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        List<ManualTimer> due = [];
        lock (_gate)
        {
            _utcNow += delta;
            foreach (ManualTimer timer in _timers)
            {
                if (timer.AdvanceAndCheckDue(delta))
                {
                    due.Add(timer);
                }
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan? _remaining;
        private TimeSpan? _period;

        internal ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _remaining = Normalize(dueTime);
            _period = Normalize(period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                _remaining = Normalize(dueTime);
                _period = Normalize(period);
            }

            return true;
        }

        internal bool AdvanceAndCheckDue(TimeSpan delta)
        {
            if (_remaining is not { } remaining)
            {
                return false;
            }

            remaining -= delta;
            if (remaining > TimeSpan.Zero)
            {
                _remaining = remaining;
                return false;
            }

            _remaining = _period is { } period && period > TimeSpan.Zero
                ? period
                : null;
            return true;
        }

        internal void Fire() => _callback.Invoke(_state);

        public void Dispose() => _owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private static TimeSpan? Normalize(TimeSpan value) =>
            value == Timeout.InfiniteTimeSpan ? null : value;
    }
}

internal sealed class RestartReadinessTimeoutProbeRunner : IInProcessProbeRunner
{
    private readonly TaskCompletionSource _restartReadinessEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readinessAttempts;

    internal Task RestartReadinessEntered => _restartReadinessEntered.Task;

    public Task<InProcessProbeResult> RunAsync(
        InProcessProbeConfiguration probe,
        ResourceContext context,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
            probe.Mapping.Role,
            "readiness",
            StringComparison.OrdinalIgnoreCase))
        {
            if (Interlocked.Increment(ref _readinessAttempts) == 1)
            {
                return Task.FromResult(InProcessProbeResult.Success("initial readiness"));
            }

            _restartReadinessEntered.TrySetResult();
            return Task.FromResult(InProcessProbeResult.Failure("restart not ready"));
        }

        return Task.FromResult(string.Equals(
            probe.Mapping.Role,
            "liveness",
            StringComparison.OrdinalIgnoreCase)
                ? InProcessProbeResult.Failure("restart requested")
                : InProcessProbeResult.Success("healthy"));
    }
}
