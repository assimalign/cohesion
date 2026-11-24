using System;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A clock that moves only when a test moves it.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration)
    {
        _now += duration;
    }
}
