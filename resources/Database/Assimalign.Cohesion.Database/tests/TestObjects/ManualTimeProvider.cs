using System;
using System.Threading;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// A clock a test moves by hand: its timestamps change only through <see cref="Advance"/>, so a
/// test crosses an engine's worker failure window (owner decision 42) without waiting for it.
/// </summary>
/// <remarks>
/// A test object deriving from the BCL <see cref="TimeProvider"/>; the repository takes no
/// <c>Microsoft.Extensions.*</c> package, so it is not <c>FakeTimeProvider</c>. The model engines'
/// test projects link this file. It drives timestamps only: nothing here creates timers.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset _epoch = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    private long _ticks;

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>
    /// Gets how far the clock has been moved since it was created.
    /// </summary>
    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <inheritdoc />
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => _epoch.AddTicks(Interlocked.Read(ref _ticks));

    /// <summary>
    /// Moves the clock forward.
    /// </summary>
    /// <param name="by">How far; not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="by"/> is negative.</exception>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        Interlocked.Add(ref _ticks, by.Ticks);
    }
}
