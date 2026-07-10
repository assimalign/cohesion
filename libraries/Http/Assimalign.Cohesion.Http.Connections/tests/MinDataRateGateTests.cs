using System;

using Assimalign.Cohesion.Http.Connections.Internal;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Unit tests for the average-rate-with-grace accounting in <see cref="MinDataRateGate"/>. The gate
/// is pure arithmetic — a fake clock supplies only the timestamp frequency (1000 ticks per second,
/// so one tick is one millisecond) — so these tests are deterministic and never sleep.
/// </summary>
public class MinDataRateGateTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRateGate: The first operation is bounded by the grace period")]
    public void TryGetOperationTimeout_BeforeAnyWait_ShouldReturnGracePeriod()
    {
        MinDataRateGate gate = new(new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromSeconds(1)), new TickClock());

        gate.TryGetOperationTimeout(out TimeSpan timeout).ShouldBeTrue();
        timeout.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRateGate: Waiting without receiving bytes exhausts the allowance")]
    public void Record_WaitingWithoutBytes_ShouldExhaustAllowance()
    {
        MinDataRateGate gate = new(new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromSeconds(1)), new TickClock());

        // Wait 600 ms of the 1000 ms grace with no bytes: 400 ms of allowance remains.
        gate.Record(ticksWaited: 600, bytesTransferred: 0);
        gate.TryGetOperationTimeout(out TimeSpan remaining).ShouldBeTrue();
        remaining.ShouldBe(TimeSpan.FromMilliseconds(400));

        // Wait 600 ms more (1200 total) — past the grace with nothing delivered: allowance is gone.
        gate.Record(ticksWaited: 600, bytesTransferred: 0);
        gate.TryGetOperationTimeout(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRateGate: Delivered octets extend the allowance at the configured rate")]
    public void Record_DeliveringBytes_ShouldExtendAllowance()
    {
        MinDataRateGate gate = new(new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromSeconds(1)), new TickClock());

        // 1000 octets at 1000 octets/s buys 1000 ms of allowance on top of the 1000 ms grace.
        // After waiting 1500 ms the peer is still on track (2000 ms allowed).
        gate.Record(ticksWaited: 1500, bytesTransferred: 1000);
        gate.GetRemainingTicks().ShouldBe(500);
        gate.TryGetOperationTimeout(out TimeSpan remaining).ShouldBeTrue();
        remaining.ShouldBe(TimeSpan.FromMilliseconds(500));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRateGate: A trickling peer falls behind once past the grace period")]
    public void GetRemainingTicks_TricklePastGrace_ShouldGoNegative()
    {
        MinDataRateGate gate = new(new HttpMinDataRate(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromSeconds(1)), new TickClock());

        // 100 octets delivered but 2000 ms spent waiting: allowed = 1000 (grace) + 100 ms (100/1000 s)
        // = 1100 ms, well short of the 2000 ms waited, so the peer is below the rate.
        gate.Record(ticksWaited: 2000, bytesTransferred: 100);
        gate.GetRemainingTicks().ShouldBeLessThan(0);
        gate.TryGetOperationTimeout(out _).ShouldBeFalse();
    }

    /// <summary>A <see cref="TimeProvider"/> whose only role here is to fix the timestamp frequency at 1000 (1 tick = 1 ms).</summary>
    private sealed class TickClock : TimeProvider
    {
        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => 0;
    }
}
