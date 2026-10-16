using System;
using System.Numerics;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Media.Tests;

public class MediaTimestampTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Media] - Exact rescaling preserves media clock values")]
    [InlineData(90000, 90000, 48000, 48000)]
    [InlineData(48000, 48000, 1000, 1000)]
    [InlineData(1000, 1000, 90000, 90000)]
    [InlineData(1001, 30000, 90000, 3003)]
    [InlineData(1001, 30000, 240000, 8008)]
    [InlineData(1, 1001, 30030000, 30000)]
    [InlineData(-3003, 90000, 30000, -1001)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue)]
    [InlineData(long.MinValue, long.MaxValue, long.MaxValue, long.MinValue)]
    public void RescaleExact_RepresentableTime_PreservesExactValue(long value, long scale, long destination, long expected)
    {
        // Arrange
        var time = new MediaTimestamp(value, scale);

        // Act
        var result = time.RescaleExact(destination);

        // Assert
        result.Value.ShouldBe(expected);
        result.Timescale.ShouldBe(destination);
        result.ShouldBe(time);
        result.RescaleExact(scale).Value.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Fractional ticks follow all signed rounding policies")]
    [InlineData(1, 2, MidpointRounding.ToEven, 0)]
    [InlineData(3, 2, MidpointRounding.ToEven, 2)]
    [InlineData(5, 2, MidpointRounding.ToEven, 2)]
    [InlineData(-1, 2, MidpointRounding.ToEven, 0)]
    [InlineData(-3, 2, MidpointRounding.ToEven, -2)]
    [InlineData(-5, 2, MidpointRounding.ToEven, -2)]
    [InlineData(1, 2, MidpointRounding.AwayFromZero, 1)]
    [InlineData(-1, 2, MidpointRounding.AwayFromZero, -1)]
    [InlineData(1, 3, MidpointRounding.AwayFromZero, 0)]
    [InlineData(-1, 3, MidpointRounding.AwayFromZero, 0)]
    [InlineData(2, 3, MidpointRounding.ToEven, 1)]
    [InlineData(-2, 3, MidpointRounding.ToEven, -1)]
    [InlineData(5, 3, MidpointRounding.ToZero, 1)]
    [InlineData(-5, 3, MidpointRounding.ToZero, -1)]
    [InlineData(5, 3, MidpointRounding.ToNegativeInfinity, 1)]
    [InlineData(-5, 3, MidpointRounding.ToNegativeInfinity, -2)]
    [InlineData(5, 3, MidpointRounding.ToPositiveInfinity, 2)]
    [InlineData(-5, 3, MidpointRounding.ToPositiveInfinity, -1)]
    public void Rescale_FractionalTicks_UsesSpecifiedRounding(long value, long scale, MidpointRounding rounding, long expected)
    {
        // Arrange
        var time = new MediaTimestamp(value, scale);

        // Act
        var result = time.Rescale(1, rounding);

        // Assert
        result.Value.ShouldBe(expected);
        result.Timescale.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - NTSC frame accumulation has no floating-point drift")]
    public void Addition_NtscFrames_AccumulatesExactly()
    {
        // Arrange
        var frame = new MediaTimestamp(1001, 30000);
        var total = default(MediaTimestamp);

        // Act
        for (var i = 0; i < 30000; i++)
        {
            total += frame;
        }

        // Assert
        total.ShouldBe(new MediaTimestamp(1001, 1));
        total.RescaleExact(48000).Value.ShouldBe(48048000);
        total.ToTimeSpan().ShouldBe(TimeSpan.FromSeconds(1001));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Arithmetic reduces wide intermediates before checking limits")]
    public void Arithmetic_ExtremeScales_ReducesBeforeCheckingBounds()
    {
        // Arrange
        var first = new MediaTimestamp(long.MaxValue, long.MaxValue);
        var second = new MediaTimestamp(long.MaxValue - 1, long.MaxValue - 1);

        // Act
        var sum = first + second;
        var difference = first - second;
        var mixed = new MediaTimestamp(1, 48000) + new MediaTimestamp(1, 90000);
        var minimum = new MediaTimestamp(long.MinValue, 1) - default(MediaTimestamp);

        // Assert
        sum.Value.ShouldBe(2);
        sum.Timescale.ShouldBe(1);
        difference.Value.ShouldBe(0);
        difference.Timescale.ShouldBe(1);
        mixed.Value.ShouldBe(23);
        mixed.Timescale.ShouldBe(720000);
        minimum.Value.ShouldBe(long.MinValue);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Comparison and arithmetic match an independent BigInteger oracle")]
    public void Arithmetic_ClockAndBoundaryCorpus_MatchesExactRationalOracle()
    {
        // Arrange
        long[] values = [long.MinValue, long.MinValue + 1, -90001, -1, 0, 1, 1001, 90001, long.MaxValue];
        long[] scales = [1, 1000, 1001, 30000, 48000, 90000, long.MaxValue - 1, long.MaxValue];

        // Act / Assert: the oracle uses unbounded integers, independently of the library's fixed-width strategy.
        foreach (var value in values)
        {
            foreach (var scale in scales)
            {
                var left = new MediaTimestamp(value, scale);
                foreach (var otherScale in scales)
                {
                    var right = new MediaTimestamp(1001, otherScale);
                    var crossLeft = (BigInteger)value * otherScale;
                    var crossRight = (BigInteger)1001 * scale;
                    Math.Sign(left.CompareTo(right)).ShouldBe(Math.Sign(crossLeft.CompareTo(crossRight)));
                    VerifyArithmetic(left, right, crossLeft + crossRight, (BigInteger)scale * otherScale, subtract: false);
                    VerifyArithmetic(left, right, crossLeft - crossRight, (BigInteger)scale * otherScale, subtract: true);
                }
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Cross-scale comparison distinguishes times closer than double precision")]
    public void CompareTo_ExtremeAdjacentTimes_DistinguishesExactOrder()
    {
        // Arrange
        var before = new MediaTimestamp(long.MaxValue - 1, long.MaxValue);
        var at = new MediaTimestamp(1, 1);
        var after = new MediaTimestamp(long.MaxValue, long.MaxValue - 1);

        // Act / Assert
        (before < at).ShouldBeTrue();
        (at <= after).ShouldBeTrue();
        (after > at).ShouldBeTrue();
        (at >= before).ShouldBeTrue();
        (before != at).ShouldBeTrue();
        (at == new MediaTimestamp(90000, 90000)).ShouldBeTrue();
        new MediaTimestamp(long.MinValue, long.MaxValue).CompareTo(after).ShouldBeNegative();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Equal times share a hash across representations")]
    [InlineData(1000, 1000, 48000, 48000)]
    [InlineData(0, 90000, 0, 1)]
    [InlineData(long.MinValue, 2, long.MinValue / 2, 1)]
    public void Equals_DifferentRepresentations_UsesTimeEquality(long first, long firstScale, long second, long secondScale)
    {
        // Arrange
        var left = new MediaTimestamp(first, firstScale);
        var right = new MediaTimestamp(second, secondScale);

        // Act / Assert
        left.Equals(right).ShouldBeTrue();
        left.Equals((object)right).ShouldBeTrue();
        left.GetHashCode().ShouldBe(right.GetHashCode());
        left.Equals(null).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Default timestamp is usable zero at timescale one")]
    public void Constructor_DefaultValue_RepresentsZero()
    {
        // Arrange
        var time = default(MediaTimestamp);

        // Act / Assert
        time.Value.ShouldBe(0);
        time.Timescale.ShouldBe(1);
        time.ShouldBe(new MediaTimestamp(0, 48000));
        time.GetHashCode().ShouldBe(new MediaTimestamp(0, 1000).GetHashCode());
        time.ToTimeSpan().ShouldBe(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Exact rescaling refuses fractional destination ticks")]
    public void RescaleExact_FractionalDestination_RejectsLoss()
    {
        // Arrange
        var frame = new MediaTimestamp(1001, 30000);

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => frame.RescaleExact(48000));
        frame.Rescale(48000).Value.ShouldBe(1602);
        frame.Rescale(1000, MidpointRounding.ToNegativeInfinity).Value.ShouldBe(33);
        frame.Rescale(1000, MidpointRounding.ToPositiveInfinity).Value.ShouldBe(34);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Unrepresentable arithmetic throws rather than wrapping")]
    public void Arithmetic_OverflowingResults_ThrowsOverflow()
    {
        // Arrange
        var maximum = new MediaTimestamp(long.MaxValue, 1);
        var minimum = new MediaTimestamp(long.MinValue, 1);
        var one = new MediaTimestamp(1, 1);
        var tiny = new MediaTimestamp(1, long.MaxValue);
        var adjacent = new MediaTimestamp(1, long.MaxValue - 1);

        // Act / Assert
        Should.Throw<OverflowException>(() => maximum + one);
        Should.Throw<OverflowException>(() => minimum - one);
        Should.Throw<OverflowException>(() => tiny - adjacent);
        Should.Throw<OverflowException>(() => maximum.RescaleExact(long.MaxValue));
        Should.Throw<OverflowException>(() => minimum.Rescale(2));
        Should.Throw<OverflowException>(() => maximum.ToTimeSpan());
        Should.Throw<OverflowException>(() => minimum.ToTimeSpan());
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - Rounding can reach either Int64 boundary without intermediate overflow")]
    public void Rescale_ExtremeScales_RoundsAtInt64Boundaries()
    {
        // Arrange
        var positive = new MediaTimestamp(long.MaxValue - 1, long.MaxValue);
        var negative = new MediaTimestamp(long.MinValue + 2, long.MaxValue);

        // Act / Assert
        positive.Rescale(long.MaxValue - 1, MidpointRounding.ToPositiveInfinity).Value.ShouldBe(long.MaxValue - 1);
        negative.Rescale(long.MaxValue - 1, MidpointRounding.ToNegativeInfinity).Value.ShouldBe(long.MinValue + 2);
        new MediaTimestamp(long.MaxValue, long.MaxValue).ToTimeSpan().ShouldBe(TimeSpan.FromSeconds(1));
        new MediaTimestamp(long.MinValue, TimeSpan.TicksPerSecond).ToTimeSpan().ShouldBe(TimeSpan.MinValue);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Nonpositive timescales are refused")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Constructor_InvalidTimescale_ThrowsArgumentOutOfRange(long scale)
    {
        // Arrange
        var valid = new MediaTimestamp(1, 1);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => new MediaTimestamp(1, scale));
        Should.Throw<ArgumentOutOfRangeException>(() => valid.Rescale(scale));
        Should.Throw<ArgumentOutOfRangeException>(() => valid.RescaleExact(scale));
    }

    [Theory(DisplayName = "Cohesion Test [Content.Media] - Unknown rounding modes are refused even for integral values")]
    [InlineData(-1)]
    [InlineData(5)]
    public void Rescale_InvalidRounding_ThrowsArgumentOutOfRange(int rounding)
    {
        // Arrange
        var time = new MediaTimestamp(1, 1);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => time.Rescale(1, (MidpointRounding)rounding));
        Should.Throw<ArgumentOutOfRangeException>(() => time.ToTimeSpan((MidpointRounding)rounding));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Media] - TimeSpan conversion applies only display precision")]
    public void ToTimeSpan_SubTickTime_RoundsWithoutChangingSource()
    {
        // Arrange
        var time = new MediaTimestamp(1, 30000000);

        // Act
        var display = time.ToTimeSpan();
        var upperDisplay = time.ToTimeSpan(MidpointRounding.ToPositiveInfinity);

        // Assert
        display.ShouldBe(TimeSpan.Zero);
        upperDisplay.Ticks.ShouldBe(1);
        time.Value.ShouldBe(1);
        time.Timescale.ShouldBe(30000000);
    }

    private static void VerifyArithmetic(MediaTimestamp left, MediaTimestamp right, BigInteger numerator, BigInteger denominator, bool subtract)
    {
        var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        numerator /= divisor;
        denominator /= divisor;
        if (numerator < long.MinValue || numerator > long.MaxValue || denominator > long.MaxValue)
        {
            Should.Throw<OverflowException>(() => subtract ? left - right : left + right);
            return;
        }
        var result = subtract ? left - right : left + right;
        result.Value.ShouldBe((long)numerator);
        result.Timescale.ShouldBe((long)denominator);
    }
}
