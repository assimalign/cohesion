using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Pins the idle timer's arithmetic (AMQP 1.0 Part 2, section 2.4.5) without waiting on a clock: the peer
/// hears from a connection within every idle timeout the connection keeps, and the connection notices a
/// silent peer soon after its own timeout.
/// </summary>
/// <remarks>
/// The timer sends an empty frame at the first tick after nothing was written for the heartbeat interval,
/// so the longest gap between two frames the peer receives is the interval plus one timer period.
/// </remarks>
public class AmqpIdleTimerTests
{
    private static readonly long[] _localTimeouts =
    [
        0,
        (long)AmqpConnectionSettings.MinIdleTimeout.TotalMilliseconds,
        1_000,
        60_000,
        (long)AmqpConnectionSettings.MaxTimeout.TotalMilliseconds
    ];

    private static readonly long[] _remoteTimeouts =
    [
        AmqpConnectionSettings.MinRemoteIdleTimeout,
        AmqpConnectionSettings.MinRemoteIdleTimeout + 1,
        150,
        199,
        200,
        201,
        1_000,
        60_000,
        240_000,
        uint.MaxValue
    ];

    public static TheoryData<long, long> KeptTimeouts()
    {
        TheoryData<long, long> data = new();

        foreach (long local in _localTimeouts)
        {
            foreach (long remote in _remoteTimeouts)
            {
                data.Add(local, remote);
            }
        }

        return data;
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - GetTimerPeriod: Should let the peer hear from the connection within every idle timeout the connection keeps")]
    [MemberData(nameof(KeptTimeouts))]
    public void GetTimerPeriod_OnKeptRemoteIdleTimeout_ShouldBoundTheLongestGap(long local, long remote)
    {
        // Act
        long period = AmqpConnectionEndpoint.GetTimerPeriod(local, remote);
        long interval = AmqpConnectionEndpoint.GetHeartbeatInterval(remote);
        long longestGap = interval + period;

        // Assert
        period.ShouldBeGreaterThanOrEqualTo(AmqpConnectionSettings.MinTimerPeriod);
        longestGap.ShouldBeLessThanOrEqualTo(remote, $"local {local} ms, remote {remote} ms");

        if (remote >= 4 * AmqpConnectionSettings.MinTimerPeriod)
        {
            longestGap.ShouldBeLessThanOrEqualTo(remote * 3 / 4, $"local {local} ms, remote {remote} ms");
        }

        if (local > 0)
        {
            period.ShouldBeLessThanOrEqualTo(local / 4, $"local {local} ms, remote {remote} ms");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - GetTimerPeriod: Should check a quarter of the local idle timeout when the peer declares none")]
    [InlineData(200, 50)]
    [InlineData(1_000, 250)]
    [InlineData(60_000, 15_000)]
    public void GetTimerPeriod_OnLocalIdleTimeoutOnly_ShouldCheckAQuarterOfIt(long local, long expected)
    {
        // Act
        long period = AmqpConnectionEndpoint.GetTimerPeriod(local, 0);

        // Assert
        period.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - GetTimerPeriod: Should run no timer when neither side has an idle timeout")]
    public void GetTimerPeriod_WithoutIdleTimeouts_ShouldReturnZero()
    {
        // Act
        long period = AmqpConnectionEndpoint.GetTimerPeriod(0, 0);

        // Assert
        period.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - GetTimerPeriod: Should not let a tiny idle timeout spin the timer")]
    public void GetTimerPeriod_OnIdleTimeoutsUnderTheFloor_ShouldReturnTheFloor()
    {
        // Act
        long period = AmqpConnectionEndpoint.GetTimerPeriod(4, 4);

        // Assert
        period.ShouldBe(AmqpConnectionSettings.MinTimerPeriod);
    }
}
