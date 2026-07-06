using System;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose "now" is set explicitly, so tests can drive key rotation
/// and the unprotect grace window without real delays.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public MutableTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;
}
