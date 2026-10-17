using System;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// The retry schedule of deferred undo (#1226 owner decision of 2026-10-04): about 100 ms after a
/// deferral, doubling after each failed retry up to the limit (the engine's maintenance
/// interval), starting over with a new deferral, and stopping once nothing is deferred.
/// </summary>
public sealed class DeferredUndoBackoffTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Deferred undo backoff: 100 ms, doubling, capped, reset by a new deferral")]
    public void Backoff_DeferralsAndFailedRetries_ShouldFollowTheSchedule()
    {
        // Arrange
        var time = new ManualTime();
        var backoff = new DeferredUndoBackoff(time) { Limit = TimeSpan.FromMilliseconds(500) };

        // Act & Assert: nothing deferred, nothing due.
        backoff.DueIn.ShouldBeNull();
        backoff.IsDue.ShouldBeFalse();

        backoff.OnDeferred();
        backoff.DueIn.ShouldBe(TimeSpan.FromMilliseconds(100));
        time.Advance(TimeSpan.FromMilliseconds(30));
        backoff.DueIn.ShouldBe(TimeSpan.FromMilliseconds(70));
        backoff.IsDue.ShouldBeFalse();
        time.Advance(TimeSpan.FromMilliseconds(70));
        backoff.IsDue.ShouldBeTrue();

        backoff.OnRetryFailed();
        backoff.DueIn.ShouldBe(TimeSpan.FromMilliseconds(200));
        backoff.OnRetryFailed();
        backoff.DueIn.ShouldBe(TimeSpan.FromMilliseconds(400));
        backoff.OnRetryFailed();
        backoff.DueIn.ShouldBe(TimeSpan.FromMilliseconds(500));
        backoff.OnRetryFailed();
        backoff.DueIn.ShouldBe(TimeSpan.FromMilliseconds(500));

        backoff.OnDeferred();
        backoff.DueIn.ShouldBe(TimeSpan.FromMilliseconds(100));

        backoff.OnCleared();
        backoff.DueIn.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Deferred undo backoff: a limit below 100 ms caps the first delay, and limits must be positive")]
    public void Backoff_ShortOrInvalidLimit_ShouldCapTheFirstDelayAndRefuseNonPositiveLimits()
    {
        // Arrange
        var backoff = new DeferredUndoBackoff(new ManualTime()) { Limit = TimeSpan.FromMilliseconds(40) };

        // Act
        backoff.OnDeferred();
        var first = backoff.DueIn;
        backoff.OnRetryFailed();
        var second = backoff.DueIn;

        // Assert
        first.ShouldBe(TimeSpan.FromMilliseconds(40));
        second.ShouldBe(TimeSpan.FromMilliseconds(40));
        Should.Throw<ArgumentOutOfRangeException>(() => backoff.Limit = TimeSpan.Zero);
        backoff.Limit = TimeSpan.MaxValue;
        backoff.OnRetryFailed();
        backoff.DueIn.ShouldBe(TimeSpan.FromMilliseconds(80));
    }

    /// <summary>A clock that moves only when the test advances it.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = TimeSpan.TicksPerDay;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
