using System;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;

/// <summary>A <see cref="TimeProvider"/> frozen at one instant, so lifetime caps are exact.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;
}
