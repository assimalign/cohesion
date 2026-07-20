using System;

namespace Assimalign.Cohesion.Web.Caching.Tests.TestObjects;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> whose clock only moves when a test advances it, so
/// time-to-live and <c>Age</c> behavior can be exercised without wall-clock sleeps.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
