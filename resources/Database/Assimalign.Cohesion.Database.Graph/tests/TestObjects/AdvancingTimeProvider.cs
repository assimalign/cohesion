using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>A deterministic clock whose UTC value advances by a day on every read.</summary>
internal sealed class AdvancingTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset _epoch = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
    private long _reads;

    internal long Reads => Interlocked.Read(ref _reads);
    internal DateTimeOffset NextUtcNow => _epoch.AddDays(Reads);

    public override DateTimeOffset GetUtcNow() => _epoch.AddDays(Interlocked.Increment(ref _reads) - 1);
}
