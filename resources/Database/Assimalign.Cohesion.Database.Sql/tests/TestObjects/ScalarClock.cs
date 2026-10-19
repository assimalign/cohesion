using System;

namespace Assimalign.Cohesion.Database.Sql.Tests;

internal sealed class ScalarClock : TimeProvider
{
    internal ScalarClock(DateTimeOffset instant) => Instant = instant;
    internal DateTimeOffset Instant { get; set; }
    public override DateTimeOffset GetUtcNow() => Instant;
}
