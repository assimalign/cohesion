using System;
using System.Threading;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose "now" is set explicitly, so tests can drive key rotation,
/// the unprotect grace window, and the unknown-key reload throttle without real delays. The
/// timestamp follows the same clock, at one timestamp unit per tick, and a thread's next
/// timestamp read can be paused so a test can interleave other callers at that point.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private readonly ThreadLocal<(ManualResetEventSlim Reached, ManualResetEventSlim Release)?> _pause = new();
    private DateTimeOffset _now;

    public MutableTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp()
    {
        if (_pause.Value is { } pause)
        {
            _pause.Value = null;
            pause.Reached.Set();
            if (!pause.Release.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The test never released the paused timestamp read.");
            }
        }

        return _now.UtcTicks;
    }

    /// <summary>
    /// Pauses the calling thread's next <see cref="GetTimestamp"/>: the read sets
    /// <paramref name="reached"/>, then waits for <paramref name="release"/> before it returns.
    /// Other threads read the timestamp without pausing.
    /// </summary>
    public void PauseNextTimestampOnCurrentThread(ManualResetEventSlim reached, ManualResetEventSlim release)
    {
        _pause.Value = (reached, release);
    }

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;
}
