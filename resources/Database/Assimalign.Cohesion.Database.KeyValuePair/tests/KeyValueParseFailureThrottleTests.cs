using System.Diagnostics;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

public sealed class KeyValueParseFailureThrottleTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Parse failure throttle: Should report the first occurrence")]
    public void TryReport_FirstFailure_ShouldReportWithoutSuppression()
    {
        // Arrange
        var throttle = new KeyValueParseFailureThrottle();

        // Act
        bool report = throttle.TryReport(0, out int suppressed);

        // Assert
        report.ShouldBeTrue();
        suppressed.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Parse failure throttle: Should hold repeats for one second and carry their count")]
    public void TryReport_RepeatedFailures_ShouldReportAtTheBoundaryAndResetSuppression()
    {
        // Arrange
        var throttle = new KeyValueParseFailureThrottle();
        long start = 1_000_000;
        throttle.TryReport(start, out _).ShouldBeTrue();

        // Act — advance a logical Stopwatch timestamp, as TcpAcceptBackoffTests does.
        for (int i = 1; i <= 9; i++)
        {
            throttle.TryReport(start + (i * Stopwatch.Frequency / 10), out int held).ShouldBeFalse();
            held.ShouldBe(0);
        }
        throttle.TryReport(start + Stopwatch.Frequency - 1, out _).ShouldBeFalse();
        bool next = throttle.TryReport(start + Stopwatch.Frequency, out int suppressed);
        bool repeat = throttle.TryReport(start + Stopwatch.Frequency + 1, out int heldAgain);
        bool third = throttle.TryReport(start + (2 * Stopwatch.Frequency), out int suppressedAgain);

        // Assert
        next.ShouldBeTrue();
        suppressed.ShouldBe(10);
        repeat.ShouldBeFalse();
        heldAgain.ShouldBe(0);
        third.ShouldBeTrue();
        suppressedAgain.ShouldBe(1);
    }
}
