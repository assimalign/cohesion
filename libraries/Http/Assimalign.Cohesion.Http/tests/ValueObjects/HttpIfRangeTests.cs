using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 13.1.5 compliance tests for <see cref="HttpIfRange"/>: distinguishing the
/// entity-tag form from the HTTP-date form and round-tripping each.
/// </summary>
public class HttpIfRangeTests
{
    [Fact]
    public void TryParse_EntityTag_ShouldCaptureTag()
    {
        HttpIfRange.TryParse("\"xyzzy\"", out HttpIfRange ifRange).ShouldBeTrue();

        ifRange.IsEntityTag.ShouldBeTrue();
        ifRange.EntityTag!.Value.Tag.ShouldBe("xyzzy");
        ifRange.Date.ShouldBeNull();
    }

    [Fact]
    public void TryParse_WeakEntityTag_ShouldCaptureWeakness()
    {
        HttpIfRange.TryParse("W/\"xyzzy\"", out HttpIfRange ifRange).ShouldBeTrue();

        ifRange.IsEntityTag.ShouldBeTrue();
        ifRange.EntityTag!.Value.IsWeak.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_HttpDate_ShouldCaptureDate()
    {
        HttpIfRange.TryParse("Sun, 06 Nov 1994 08:49:37 GMT", out HttpIfRange ifRange).ShouldBeTrue();

        ifRange.IsEntityTag.ShouldBeFalse();
        ifRange.Date.ShouldBe(new DateTimeOffset(1994, 11, 6, 8, 49, 37, TimeSpan.Zero));
    }

    [Theory]
    // RFC 9110 § 5.6.7: recipients MUST accept all three HTTP-date formats. All three encode the
    // same instant (a Wednesday, so the RFC 850 / asctime forms also exercise the "Wed" vs "W/"
    // entity-tag disambiguation).
    [InlineData("Wed, 01 Jan 2020 00:00:00 GMT")]     // IMF-fixdate
    [InlineData("Wednesday, 01-Jan-20 00:00:00 GMT")] // obsolete RFC 850
    [InlineData("Wed Jan  1 00:00:00 2020")]          // asctime (space-padded day)
    public void TryParse_AllThreeDateFormats_ShouldParseToSameInstant(string raw)
    {
        HttpIfRange.TryParse(raw, out HttpIfRange ifRange).ShouldBeTrue();

        ifRange.IsEntityTag.ShouldBeFalse();
        ifRange.Date.ShouldBe(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("\"unterminated")]
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        HttpIfRange.TryParse(raw, out HttpIfRange ifRange).ShouldBeFalse();
        ifRange.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ToString_EntityTag_ShouldRoundTrip()
    {
        HttpIfRange.Parse("W/\"abc\"").ToString().ShouldBe("W/\"abc\"");
    }

    [Fact]
    public void ToString_Date_ShouldEmitImfFixdate()
    {
        HttpIfRange ifRange = HttpIfRange.FromDate(new DateTimeOffset(1994, 11, 6, 8, 49, 37, TimeSpan.Zero));

        ifRange.ToString().ShouldBe("Sun, 06 Nov 1994 08:49:37 GMT");
    }

    // ============================================================================
    // Matches (RFC 9110 § 13.1.5 / § 13.2.2 step 5) — the range-application decision
    // ============================================================================

    private static readonly DateTimeOffset LastModified = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Matches_EntityTagStrongMatch_ShouldApplyRange()
    {
        HttpIfRange ifRange = HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1"));

        ifRange.Matches(HttpEntityTag.Strong("v1"), LastModified).ShouldBeTrue();
    }

    [Fact]
    public void Matches_EntityTagMismatch_ShouldIgnoreRange()
    {
        HttpIfRange ifRange = HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1"));

        ifRange.Matches(HttpEntityTag.Strong("v2"), LastModified).ShouldBeFalse();
    }

    [Fact]
    public void Matches_WeakCurrentTag_ShouldIgnoreRange()
    {
        // If-Range uses strong comparison, so a weak current validator never applies the range.
        HttpIfRange ifRange = HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1"));

        ifRange.Matches(HttpEntityTag.Weak("v1"), LastModified).ShouldBeFalse();
    }

    [Fact]
    public void Matches_EntityTagButNoCurrentTag_ShouldIgnoreRange()
    {
        HttpIfRange ifRange = HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1"));

        ifRange.Matches(currentETag: null, LastModified).ShouldBeFalse();
    }

    [Fact]
    public void Matches_DateEqualsLastModified_ShouldApplyRange()
    {
        HttpIfRange.FromDate(LastModified).Matches(null, LastModified).ShouldBeTrue();
    }

    [Fact]
    public void Matches_DateOlderThanLastModified_ShouldIgnoreRange()
    {
        // Representation was modified after the client's date → serve the full 200.
        HttpIfRange ifRange = HttpIfRange.FromDate(new DateTimeOffset(2019, 6, 1, 0, 0, 0, TimeSpan.Zero));

        ifRange.Matches(null, LastModified).ShouldBeFalse();
    }

    [Fact]
    public void Matches_DateNewerThanLastModified_ShouldApplyRange()
    {
        HttpIfRange ifRange = HttpIfRange.FromDate(new DateTimeOffset(2020, 6, 1, 0, 0, 0, TimeSpan.Zero));

        ifRange.Matches(null, LastModified).ShouldBeTrue();
    }

    [Fact]
    public void Matches_SubSecondLastModification_ShouldStillApplyRange()
    {
        // A sub-second bump does not count as modified at HTTP-date (one-second) granularity.
        HttpIfRange.FromDate(LastModified).Matches(null, LastModified.AddMilliseconds(500)).ShouldBeTrue();
    }

    [Fact]
    public void Matches_DateFormButNoLastModified_ShouldIgnoreRange()
    {
        HttpIfRange.FromDate(LastModified).Matches(HttpEntityTag.Strong("v1"), currentLastModified: null).ShouldBeFalse();
    }
}
