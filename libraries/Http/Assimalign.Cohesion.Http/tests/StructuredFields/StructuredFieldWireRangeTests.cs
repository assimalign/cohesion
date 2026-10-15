using System;
using System.Globalization;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class StructuredFieldWireRangeTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - FromDecimal: Should reject values that round outside the wire range")]
    [InlineData("999999999999.9995")]
    [InlineData("999999999999.9999")]
    [InlineData("-999999999999.9995")]
    [InlineData("-999999999999.9999")]
    public void FromDecimal_RoundingOutsideWireRange_ShouldThrow(string input)
    {
        // Arrange
        decimal value = decimal.Parse(input, CultureInfo.InvariantCulture);

        // Act
        var exception = Should.Throw<ArgumentOutOfRangeException>(() => StructuredFieldBareItem.FromDecimal(value));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.ActualValue.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - FromDecimal: Should round-trip valid wire boundaries")]
    [InlineData("999999999999.999", "999999999999.999")]
    [InlineData("-999999999999.999", "-999999999999.999")]
    [InlineData("999999999999.9994", "999999999999.999")]
    [InlineData("-999999999999.9994", "-999999999999.999")]
    [InlineData("0.0025", "0.002")]
    [InlineData("0.0035", "0.004")]
    public void FromDecimal_ValidWireBoundary_ShouldRoundTrip(string input, string expected)
    {
        // Arrange
        decimal value = decimal.Parse(input, CultureInfo.InvariantCulture);
        var item = StructuredFieldBareItem.FromDecimal(value);

        // Act
        string wire = item.Serialize();
        var parsed = StructuredFieldItem.Parse(wire);

        // Assert
        item.AsDecimal().ShouldBe(value);
        wire.ShouldBe(expected);
        parsed.Value.AsDecimal().ShouldBe(decimal.Parse(expected, CultureInfo.InvariantCulture));
        parsed.Serialize().ShouldBe(wire);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - FromDisplayString: Should reject unpaired surrogates at construction")]
    [InlineData(0xD800)]
    [InlineData(0xDBFF)]
    [InlineData(0xDC00)]
    [InlineData(0xDFFF)]
    public void FromDisplayString_UnpairedSurrogate_ShouldThrow(int codeUnit)
    {
        // Arrange: build the invalid UTF-16 here so test-data serialization cannot replace it.
        string surrogate = ((char)codeUnit).ToString();
        string[] values = [surrogate, "before" + surrogate, surrogate + "after", surrogate + surrogate];

        // Act / Assert
        foreach (string value in values)
        {
            var exception = Should.Throw<ArgumentException>(() => StructuredFieldBareItem.FromDisplayString(value));
            exception.ParamName.ShouldBe("value");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http] - FromDisplayString: Should round-trip Unicode scalar boundaries")]
    [InlineData("")]
    [InlineData("\u0000\uD7FF\uE000\uFFFF")]
    [InlineData("\U00010000\U0010FFFF")]
    [InlineData("before\U0001F600after")]
    public void FromDisplayString_UnicodeScalarBoundary_ShouldRoundTrip(string value)
    {
        // Arrange
        var item = StructuredFieldBareItem.FromDisplayString(value);

        // Act
        string wire = item.Serialize();
        var parsed = StructuredFieldItem.Parse(wire);

        // Assert
        parsed.Value.AsDisplayString().ShouldBe(value);
        parsed.Serialize().ShouldBe(wire);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - FromInteger: Should reject values outside the wire range")]
    [InlineData(-1_000_000_000_000_000L)]
    [InlineData(1_000_000_000_000_000L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void FromInteger_OutsideWireRange_ShouldThrow(long value)
    {
        // Act
        var exception = Should.Throw<ArgumentOutOfRangeException>(() => StructuredFieldBareItem.FromInteger(value));

        // Assert
        exception.ParamName.ShouldBe("value");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - FromInteger: Should round-trip valid wire boundaries")]
    [InlineData(-999_999_999_999_999L)]
    [InlineData(0L)]
    [InlineData(999_999_999_999_999L)]
    public void FromInteger_ValidWireBoundary_ShouldRoundTrip(long value)
    {
        // Arrange
        var item = StructuredFieldBareItem.FromInteger(value);

        // Act
        string wire = item.Serialize();
        var parsed = StructuredFieldItem.Parse(wire);

        // Assert
        parsed.Value.AsInteger().ShouldBe(value);
        parsed.Serialize().ShouldBe(wire);
    }
}
