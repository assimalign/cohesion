using System;

namespace Assimalign.Cohesion.Caching.InMemory.Tests;

/// <summary>
/// Manually advanced <see cref="TimeProvider"/> for deterministic expiration tests.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset initial)
    {
        _now = initial;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta)
    {
        _now = _now.Add(delta);
    }
}
