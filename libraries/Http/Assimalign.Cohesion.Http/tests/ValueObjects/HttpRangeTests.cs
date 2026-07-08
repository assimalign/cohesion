using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 14.1.1 / &#167; 14.1.2 compliance tests for <see cref="HttpRange"/> and
/// <see cref="HttpRangeHeader"/>: int-range, open-ended, and suffix-range parsing; multiple ranges;
/// strict rejection of malformed sets and unknown units; and single-range resolution against a
/// known content length.
/// </summary>
public class HttpRangeTests
{
    // ============================================================================
    // Single range-spec parsing
    // ============================================================================

    [Fact]
    public void TryParse_ClosedIntRange_ShouldCaptureBounds()
    {
        HttpRange.TryParse("0-499", out HttpRange range).ShouldBeTrue();

        range.From.ShouldBe(0);
        range.To.ShouldBe(499);
        range.IsSuffix.ShouldBeFalse();
        range.IsOpenEnded.ShouldBeFalse();
    }

    [Fact]
    public void TryParse_OpenEndedIntRange_ShouldHaveNoEnd()
    {
        HttpRange.TryParse("500-", out HttpRange range).ShouldBeTrue();

        range.From.ShouldBe(500);
        range.To.ShouldBeNull();
        range.IsOpenEnded.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_SuffixRange_ShouldCaptureLength()
    {
        HttpRange.TryParse("-500", out HttpRange range).ShouldBeTrue();

        range.IsSuffix.ShouldBeTrue();
        range.SuffixLength.ShouldBe(500);
        range.From.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("abc")]
    [InlineData("10-abc")]
    [InlineData("abc-10")]
    [InlineData("499-0")]     // last < first
    [InlineData("1-2-3")]     // extra dash
    [InlineData("- 5")]       // internal space
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        HttpRange.TryParse(raw, out HttpRange range).ShouldBeFalse();
        range.IsEmpty.ShouldBeTrue();
    }

    // ============================================================================
    // Resolution against content length (RFC 9110 § 14.1.2)
    // ============================================================================

    [Fact]
    public void TryResolve_ClosedRangeWithinContent_ShouldReturnExactSlice()
    {
        HttpRange.FromTo(0, 499).TryResolve(1000, out long offset, out long length).ShouldBeTrue();

        offset.ShouldBe(0);
        length.ShouldBe(500);
    }

    [Fact]
    public void TryResolve_ClosedRangePastEnd_ShouldClampToContent()
    {
        // bytes=500-1000 against a 1000-byte body clamps the last position to 999.
        HttpRange.FromTo(500, 1000).TryResolve(1000, out long offset, out long length).ShouldBeTrue();

        offset.ShouldBe(500);
        length.ShouldBe(500);
    }

    [Fact]
    public void TryResolve_OpenEnded_ShouldSpanToEnd()
    {
        HttpRange.StartingAt(900).TryResolve(1000, out long offset, out long length).ShouldBeTrue();

        offset.ShouldBe(900);
        length.ShouldBe(100);
    }

    [Fact]
    public void TryResolve_SuffixLongerThanContent_ShouldReturnWholeBody()
    {
        HttpRange.Suffix(5000).TryResolve(1000, out long offset, out long length).ShouldBeTrue();

        offset.ShouldBe(0);
        length.ShouldBe(1000);
    }

    [Fact]
    public void TryResolve_Suffix_ShouldReturnTrailingBytes()
    {
        HttpRange.Suffix(200).TryResolve(1000, out long offset, out long length).ShouldBeTrue();

        offset.ShouldBe(800);
        length.ShouldBe(200);
    }

    [Fact]
    public void TryResolve_FirstPosAtOrBeyondEnd_ShouldBeUnsatisfiable()
    {
        HttpRange.StartingAt(1000).TryResolve(1000, out _, out _).ShouldBeFalse();
        HttpRange.FromTo(1500, 2000).TryResolve(1000, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_ZeroSuffix_ShouldBeUnsatisfiable()
    {
        HttpRange.Suffix(0).TryResolve(1000, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_EmptyContent_ShouldBeUnsatisfiable()
    {
        HttpRange.FromTo(0, 10).TryResolve(0, out _, out _).ShouldBeFalse();
        HttpRange.Suffix(10).TryResolve(0, out _, out _).ShouldBeFalse();
    }

    // ============================================================================
    // Full Range header parsing
    // ============================================================================

    [Fact]
    public void TryParse_MultipleRanges_ShouldPreserveOrder()
    {
        HttpRangeHeader.TryParse("bytes=0-499, 500-999 , -500", out HttpRangeHeader header).ShouldBeTrue();

        header.Unit.ShouldBe("bytes");
        header.Count.ShouldBe(3);
        header.Ranges[0].ShouldBe(HttpRange.FromTo(0, 499));
        header.Ranges[1].ShouldBe(HttpRange.FromTo(500, 999));
        header.Ranges[2].ShouldBe(HttpRange.Suffix(500));
    }

    [Fact]
    public void TryParse_EmptyListElements_ShouldBeSkipped()
    {
        HttpRangeHeader.TryParse("bytes=0-0,,1-1", out HttpRangeHeader header).ShouldBeTrue();

        header.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bytes=")]
    [InlineData("bytes")]
    [InlineData("bytes=abc")]
    [InlineData("bytes=0-499,bad")]   // one malformed member invalidates the whole set
    [InlineData("items=0-499")]       // unknown unit
    [InlineData("=0-499")]
    public void TryParse_MalformedOrUnknownUnit_ShouldFail(string raw)
    {
        HttpRangeHeader.TryParse(raw, out HttpRangeHeader header).ShouldBeFalse();
        header.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_UnitIsCaseInsensitive()
    {
        HttpRangeHeader.TryParse("BYTES=0-1", out HttpRangeHeader header).ShouldBeTrue();
        header.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("bytes=0-499,-500")]
    [InlineData("bytes=500-")]
    public void ToString_ShouldRoundTrip(string raw)
    {
        HttpRangeHeader header = HttpRangeHeader.Parse(raw);

        header.ToString().ShouldBe(raw);
    }
}
