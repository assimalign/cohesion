using System;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose "now" is set explicitly, so tests can drive key rotation,
/// the unprotect grace window, and the unknown-key reload throttle without real delays. The
/// timestamp follows the same clock, at one timestamp unit per tick.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public MutableTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;
}
