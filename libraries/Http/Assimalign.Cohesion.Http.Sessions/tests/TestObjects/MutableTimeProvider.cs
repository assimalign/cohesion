using System;

namespace Assimalign.Cohesion.Http.Tests.TestObjects;

/// <summary>
/// A <see cref="TimeProvider"/> whose current instant is settable, so tests can
/// drive session idle-expiration deterministically without wall-clock waits.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public MutableTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
