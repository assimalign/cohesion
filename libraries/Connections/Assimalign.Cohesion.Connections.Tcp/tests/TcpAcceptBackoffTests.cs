using System;
using System.Collections.Generic;
using System.Diagnostics;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

public class TcpAcceptBackoffTests
{
    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpAcceptBackoff: Should wait 5 ms first and double up to 1 s")]
    public void NextDelay_ConsecutiveFailures_ShouldDoubleFromFiveMillisecondsUpToOneSecond()
    {
        // Arrange
        List<double> delays = [];
        TimeSpan delay = TimeSpan.Zero;

        // Act
        for (int i = 0; i < 12; i++)
        {
            delay = TcpAcceptBackoff.NextDelay(delay);
            delays.Add(delay.TotalMilliseconds);
        }

        // Assert
        delays.ShouldBe([5, 10, 20, 40, 80, 160, 320, 640, 1000, 1000, 1000, 1000]);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpAcceptBackoff: Should report the first back-off")]
    public void TryReport_FirstBackoff_ShouldReportWithNothingUnreported()
    {
        // Arrange
        TcpAcceptBackoff backoff = new();

        // Act
        bool reported = backoff.TryReport(Stopwatch.GetTimestamp(), out int unreported);

        // Assert
        reported.ShouldBeTrue();
        unreported.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpAcceptBackoff: Should report at most once a second and count what it held back")]
    public void TryReport_BackoffsWithinOneSecond_ShouldReportOnceAndCountTheRest()
    {
        // Arrange
        TcpAcceptBackoff backoff = new();
        long start = 1_000_000;
        long tenthOfASecond = Stopwatch.Frequency / 10;

        // Act
        bool first = backoff.TryReport(start, out _);
        bool[] held = new bool[9];

        for (int i = 0; i < held.Length; i++)
        {
            held[i] = backoff.TryReport(start + ((i + 1) * tenthOfASecond) - 1, out _);
        }

        bool next = backoff.TryReport(start + Stopwatch.Frequency, out int unreported);
        bool afterNext = backoff.TryReport(start + Stopwatch.Frequency + 1, out int unreportedAfterNext);

        // Assert
        first.ShouldBeTrue();
        held.ShouldAllBe(reported => !reported);
        next.ShouldBeTrue("a full report interval has passed since the last report");
        unreported.ShouldBe(9);
        afterNext.ShouldBeFalse();
        unreportedAfterNext.ShouldBe(0);
    }
}
