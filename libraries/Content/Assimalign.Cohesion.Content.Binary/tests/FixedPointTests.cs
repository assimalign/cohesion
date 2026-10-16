using System;
using System.Globalization;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Binary.Tests;

public class FixedPointTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint16x16: raw extremes round-trip exactly")]
    public void FixedPoint16x16_RawExtremes_RoundTrips()
    {
        // Arrange
        int[] rawValues = [int.MinValue, 0, 1, -1, int.MaxValue];

        // Act / Assert
        foreach (var raw in rawValues)
        {
            var value = FixedPoint16x16.FromRaw(raw);
            value.ToDouble().ShouldBe(raw / 65536.0);
            FixedPoint16x16.FromDouble(value.ToDouble()).RawValue.ShouldBe(raw);
            value.Equals(FixedPoint16x16.FromRaw(raw)).ShouldBeTrue();
            value.Equals((object)FixedPoint16x16.FromRaw(raw)).ShouldBeTrue();
            value.GetHashCode().ShouldBe(FixedPoint16x16.FromRaw(raw).GetHashCode());
            value.Equals((object)"unrelated").ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint16x16: nearest rounding uses ties to even")]
    public void FixedPoint16x16_HalfSteps_RoundsToEven()
    {
        // Arrange / Act / Assert
        FixedPoint16x16.FromDouble(0.5 / 65536.0).RawValue.ShouldBe((int)0);
        FixedPoint16x16.FromDouble(1.5 / 65536.0).RawValue.ShouldBe((int)2);
        FixedPoint16x16.FromDouble(2.5 / 65536.0).RawValue.ShouldBe((int)2);
        FixedPoint16x16.FromDouble(-1.5 / 65536.0).RawValue.ShouldBe((int)-2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint16x16: invalid values report argument range errors")]
    public void FixedPoint16x16_NonfiniteOrOutsideRange_Rejects()
    {
        // Arrange
        double[] invalid = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue,
            (int.MaxValue / 65536.0) + (1 / 65536.0), (int.MinValue / 65536.0) - (1 / 65536.0),
            (int.MaxValue / 65536.0) + (0.25 / 65536.0), (int.MinValue / 65536.0) - (0.25 / 65536.0)];

        // Act / Assert
        foreach (var value in invalid)
        {
            Should.Throw<ArgumentOutOfRangeException>(() => FixedPoint16x16.FromDouble(value));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UnsignedFixedPoint16x16: raw extremes round-trip exactly")]
    public void UnsignedFixedPoint16x16_RawExtremes_RoundTrips()
    {
        // Arrange
        uint[] rawValues = [uint.MinValue, 0, 1, uint.MaxValue];

        // Act / Assert
        foreach (var raw in rawValues)
        {
            var value = UnsignedFixedPoint16x16.FromRaw(raw);
            value.ToDouble().ShouldBe(raw / 65536.0);
            UnsignedFixedPoint16x16.FromDouble(value.ToDouble()).RawValue.ShouldBe(raw);
            value.Equals(UnsignedFixedPoint16x16.FromRaw(raw)).ShouldBeTrue();
            value.Equals((object)UnsignedFixedPoint16x16.FromRaw(raw)).ShouldBeTrue();
            value.GetHashCode().ShouldBe(UnsignedFixedPoint16x16.FromRaw(raw).GetHashCode());
            value.Equals((object)"unrelated").ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UnsignedFixedPoint16x16: nearest rounding uses ties to even")]
    public void UnsignedFixedPoint16x16_HalfSteps_RoundsToEven()
    {
        // Arrange / Act / Assert
        UnsignedFixedPoint16x16.FromDouble(0.5 / 65536.0).RawValue.ShouldBe((uint)0);
        UnsignedFixedPoint16x16.FromDouble(1.5 / 65536.0).RawValue.ShouldBe((uint)2);
        UnsignedFixedPoint16x16.FromDouble(2.5 / 65536.0).RawValue.ShouldBe((uint)2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UnsignedFixedPoint16x16: invalid values report argument range errors")]
    public void UnsignedFixedPoint16x16_NonfiniteOrOutsideRange_Rejects()
    {
        // Arrange
        double[] invalid = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue,
            (uint.MaxValue / 65536.0) + (1 / 65536.0), (uint.MinValue / 65536.0) - (1 / 65536.0),
            (uint.MaxValue / 65536.0) + (0.25 / 65536.0), (uint.MinValue / 65536.0) - (0.25 / 65536.0)];

        // Act / Assert
        foreach (var value in invalid)
        {
            Should.Throw<ArgumentOutOfRangeException>(() => UnsignedFixedPoint16x16.FromDouble(value));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint8x8: raw extremes round-trip exactly")]
    public void FixedPoint8x8_RawExtremes_RoundTrips()
    {
        // Arrange
        short[] rawValues = [short.MinValue, 0, 1, -1, short.MaxValue];

        // Act / Assert
        foreach (var raw in rawValues)
        {
            var value = FixedPoint8x8.FromRaw(raw);
            value.ToDouble().ShouldBe(raw / 256.0);
            FixedPoint8x8.FromDouble(value.ToDouble()).RawValue.ShouldBe(raw);
            value.Equals(FixedPoint8x8.FromRaw(raw)).ShouldBeTrue();
            value.Equals((object)FixedPoint8x8.FromRaw(raw)).ShouldBeTrue();
            value.GetHashCode().ShouldBe(FixedPoint8x8.FromRaw(raw).GetHashCode());
            value.Equals((object)"unrelated").ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint8x8: nearest rounding uses ties to even")]
    public void FixedPoint8x8_HalfSteps_RoundsToEven()
    {
        // Arrange / Act / Assert
        FixedPoint8x8.FromDouble(0.5 / 256.0).RawValue.ShouldBe((short)0);
        FixedPoint8x8.FromDouble(1.5 / 256.0).RawValue.ShouldBe((short)2);
        FixedPoint8x8.FromDouble(2.5 / 256.0).RawValue.ShouldBe((short)2);
        FixedPoint8x8.FromDouble(-1.5 / 256.0).RawValue.ShouldBe((short)-2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint8x8: invalid values report argument range errors")]
    public void FixedPoint8x8_NonfiniteOrOutsideRange_Rejects()
    {
        // Arrange
        double[] invalid = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue,
            (short.MaxValue / 256.0) + (1 / 256.0), (short.MinValue / 256.0) - (1 / 256.0),
            (short.MaxValue / 256.0) + (0.25 / 256.0), (short.MinValue / 256.0) - (0.25 / 256.0)];

        // Act / Assert
        foreach (var value in invalid)
        {
            Should.Throw<ArgumentOutOfRangeException>(() => FixedPoint8x8.FromDouble(value));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint2x30: raw extremes round-trip exactly")]
    public void FixedPoint2x30_RawExtremes_RoundTrips()
    {
        // Arrange
        int[] rawValues = [int.MinValue, 0, 1, -1, int.MaxValue];

        // Act / Assert
        foreach (var raw in rawValues)
        {
            var value = FixedPoint2x30.FromRaw(raw);
            value.ToDouble().ShouldBe(raw / 1073741824.0);
            FixedPoint2x30.FromDouble(value.ToDouble()).RawValue.ShouldBe(raw);
            value.Equals(FixedPoint2x30.FromRaw(raw)).ShouldBeTrue();
            value.Equals((object)FixedPoint2x30.FromRaw(raw)).ShouldBeTrue();
            value.GetHashCode().ShouldBe(FixedPoint2x30.FromRaw(raw).GetHashCode());
            value.Equals((object)"unrelated").ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint2x30: nearest rounding uses ties to even")]
    public void FixedPoint2x30_HalfSteps_RoundsToEven()
    {
        // Arrange / Act / Assert
        FixedPoint2x30.FromDouble(0.5 / 1073741824.0).RawValue.ShouldBe((int)0);
        FixedPoint2x30.FromDouble(1.5 / 1073741824.0).RawValue.ShouldBe((int)2);
        FixedPoint2x30.FromDouble(2.5 / 1073741824.0).RawValue.ShouldBe((int)2);
        FixedPoint2x30.FromDouble(-1.5 / 1073741824.0).RawValue.ShouldBe((int)-2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint2x30: invalid values report argument range errors")]
    public void FixedPoint2x30_NonfiniteOrOutsideRange_Rejects()
    {
        // Arrange
        double[] invalid = [double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue,
            (int.MaxValue / 1073741824.0) + (1 / 1073741824.0), (int.MinValue / 1073741824.0) - (1 / 1073741824.0),
            (int.MaxValue / 1073741824.0) + (0.25 / 1073741824.0), (int.MinValue / 1073741824.0) - (0.25 / 1073741824.0)];

        // Act / Assert
        foreach (var value in invalid)
        {
            Should.Throw<ArgumentOutOfRangeException>(() => FixedPoint2x30.FromDouble(value));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Fixed point: diagnostic text is culture independent")]
    public void ToString_FrenchCulture_UsesInvariantDecimalPoint()
    {
        // Arrange
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            // Act / Assert
            FixedPoint16x16.FromDouble(1.5).ToString().ShouldBe("1.5");
            UnsignedFixedPoint16x16.FromDouble(1.5).ToString().ShouldBe("1.5");
            FixedPoint8x8.FromDouble(1.5).ToString().ShouldBe("1.5");
            FixedPoint2x30.FromDouble(1.5).ToString().ShouldBe("1.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
