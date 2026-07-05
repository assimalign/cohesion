using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 12.4.2 compliance tests for <see cref="HttpQuality"/> quality-value parsing,
/// exact comparison, and ordering.
/// </summary>
public class HttpQualityTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1000)]
    [InlineData("0.5", 500)]
    [InlineData("0.500", 500)]
    [InlineData("0.333", 333)]
    [InlineData("0.001", 1)]
    [InlineData("1.0", 1000)]
    [InlineData("1.000", 1000)]
    [InlineData("0.0", 0)]
    [InlineData(" 0.8 ", 800)]
    public void TryParse_ValidWeights_ShouldParse(string raw, int expectedPerMille)
    {
        HttpQuality.TryParse(raw, out HttpQuality quality).ShouldBeTrue();

        quality.PerMille.ShouldBe(expectedPerMille);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("1.001")]
    [InlineData("1.5")]
    [InlineData("0.5000")]
    [InlineData("-0.1")]
    [InlineData("abc")]
    [InlineData("0.5a")]
    [InlineData(".5")]
    [InlineData("0,5")]
    public void TryParse_InvalidWeights_ShouldFail(string raw)
    {
        HttpQuality.TryParse(raw, out HttpQuality quality).ShouldBeFalse();
        quality.ShouldBe(default(HttpQuality));
    }

    [Fact]
    public void Constants_ShouldHaveExpectedWeights()
    {
        HttpQuality.Zero.PerMille.ShouldBe(0);
        HttpQuality.Zero.IsAcceptable.ShouldBeFalse();
        HttpQuality.One.PerMille.ShouldBe(1000);
        HttpQuality.One.IsAcceptable.ShouldBeTrue();
        HttpQuality.One.Value.ShouldBe(1.0);
    }

    [Fact]
    public void Comparison_ShouldOrderByWeight()
    {
        HttpQuality.TryParse("0.3", out HttpQuality low).ShouldBeTrue();
        HttpQuality.TryParse("0.7", out HttpQuality high).ShouldBeTrue();
        HttpQuality.TryParse("0.300", out HttpQuality lowAgain).ShouldBeTrue();

        (low < high).ShouldBeTrue();
        (high > low).ShouldBeTrue();
        low.CompareTo(high).ShouldBeLessThan(0);
        (low == lowAgain).ShouldBeTrue();
        low.Equals(lowAgain).ShouldBeTrue();
    }

    [Fact]
    public void FromPerMille_ShouldClampToRange()
    {
        HttpQuality.FromPerMille(-50).PerMille.ShouldBe(0);
        HttpQuality.FromPerMille(2000).PerMille.ShouldBe(1000);
        HttpQuality.FromPerMille(250).PerMille.ShouldBe(250);
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("0", "0")]
    [InlineData("0.5", "0.5")]
    [InlineData("0.333", "0.333")]
    public void ToString_ShouldRenderWithoutTrailingZeros(string raw, string expected)
    {
        HttpQuality.TryParse(raw, out HttpQuality quality).ShouldBeTrue();

        quality.ToString().ShouldBe(expected);
    }
}
