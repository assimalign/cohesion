using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 14.4 compliance tests for <see cref="HttpContentRange"/>: the satisfied form (with
/// a known or unknown complete length) and the unsatisfied <c>bytes */N</c> form that accompanies a
/// <c>416</c> response.
/// </summary>
public class HttpContentRangeTests
{
    [Fact]
    public void TryParse_SatisfiedWithLength_ShouldCaptureAllParts()
    {
        HttpContentRange.TryParse("bytes 0-499/1234", out HttpContentRange range).ShouldBeTrue();

        range.IsUnsatisfied.ShouldBeFalse();
        range.From.ShouldBe(0);
        range.To.ShouldBe(499);
        range.Length.ShouldBe(1234);
        range.HasCompleteLength.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_SatisfiedUnknownLength_ShouldHaveNullLength()
    {
        HttpContentRange.TryParse("bytes 0-499/*", out HttpContentRange range).ShouldBeTrue();

        range.From.ShouldBe(0);
        range.To.ShouldBe(499);
        range.Length.ShouldBeNull();
        range.HasCompleteLength.ShouldBeFalse();
    }

    [Fact]
    public void TryParse_Unsatisfied_ShouldExposeCompleteLengthOnly()
    {
        HttpContentRange.TryParse("bytes */1234", out HttpContentRange range).ShouldBeTrue();

        range.IsUnsatisfied.ShouldBeTrue();
        range.From.ShouldBeNull();
        range.To.ShouldBeNull();
        range.Length.ShouldBe(1234);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bytes")]
    [InlineData("bytes 0-499")]      // missing "/length"
    [InlineData("bytes */*")]        // unsatisfied form requires a length
    [InlineData("bytes 499-0/1234")] // last < first
    [InlineData("bytes 0-1234/1000")]// last must be < complete-length
    [InlineData("items 0-1/2")]      // unknown unit
    [InlineData("bytes 0-/10")]      // missing last-pos
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        HttpContentRange.TryParse(raw, out HttpContentRange range).ShouldBeFalse();
        range.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Satisfied_LastPosNotLessThanCompleteLength_ShouldThrow()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => HttpContentRange.Satisfied(0, 1000, 1000));
    }

    [Theory]
    [InlineData("bytes 0-499/1234")]
    [InlineData("bytes 0-499/*")]
    [InlineData("bytes */1234")]
    public void ToString_ShouldRoundTrip(string raw)
    {
        HttpContentRange range = HttpContentRange.Parse(raw);

        range.ToString().ShouldBe(raw);
    }

    [Fact]
    public void Unsatisfied_Factory_ShouldRenderStarForm()
    {
        HttpContentRange.Unsatisfied(1234).ToString().ShouldBe("bytes */1234");
    }
}
