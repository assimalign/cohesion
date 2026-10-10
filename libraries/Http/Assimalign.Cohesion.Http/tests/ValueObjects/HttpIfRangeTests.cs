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

    private static readonly DateTimeOffset _lastModified = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // A response sent a day after the last modification: Last-Modified is a strong validator.
    private static readonly DateTimeOffset _responseDate = _lastModified.AddDays(1);

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: a strongly equal entity-tag applies the range")]
    public void Matches_EntityTagStrongMatch_ShouldApplyRange()
    {
        // Arrange
        HttpIfRange ifRange = HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1"));

        // Act
        bool applies = ifRange.Matches(HttpEntityTag.Strong("v1"), _lastModified, _responseDate);

        // Assert
        applies.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: a different entity-tag ignores the range")]
    public void Matches_EntityTagMismatch_ShouldIgnoreRange()
    {
        // Arrange
        HttpIfRange ifRange = HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1"));

        // Act
        bool applies = ifRange.Matches(HttpEntityTag.Strong("v2"), _lastModified, _responseDate);

        // Assert
        applies.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: a weak current entity-tag ignores the range")]
    public void Matches_WeakCurrentTag_ShouldIgnoreRange()
    {
        // Arrange — If-Range uses strong comparison, so a weak current validator never applies the range.
        HttpIfRange ifRange = HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1"));

        // Act
        bool applies = ifRange.Matches(HttpEntityTag.Weak("v1"), _lastModified, _responseDate);

        // Assert
        applies.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: an entity-tag with no current tag ignores the range")]
    public void Matches_EntityTagButNoCurrentTag_ShouldIgnoreRange()
    {
        // Arrange
        HttpIfRange ifRange = HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1"));

        // Act
        bool applies = ifRange.Matches(currentETag: null, _lastModified, _responseDate);

        // Assert
        applies.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: the entity-tag form does not read the response date")]
    public void Matches_EntityTagWithLastModifiedInResponseSecond_ShouldApplyRange()
    {
        // Arrange — the date-strength rule belongs to the date form only; a strong entity-tag is
        // strong whenever the representation was modified.
        HttpIfRange ifRange = HttpIfRange.FromEntityTag(HttpEntityTag.Strong("v1"));

        // Act
        bool applies = ifRange.Matches(HttpEntityTag.Strong("v1"), _lastModified, responseDate: _lastModified);

        // Assert
        applies.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: a date equal to a strong Last-Modified applies the range")]
    public void Matches_DateEqualsStrongLastModified_ShouldApplyRange()
    {
        // Arrange
        HttpIfRange ifRange = HttpIfRange.FromDate(_lastModified);

        // Act
        bool applies = ifRange.Matches(null, _lastModified, _responseDate);

        // Assert
        applies.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: a date other than Last-Modified ignores the range")]
    [InlineData(-86_400)] // earlier: the representation changed after the client's copy
    [InlineData(-1)]
    [InlineData(1)]       // later: a file replaced by an older copy, or a client clock ahead of the server's
    [InlineData(86_400)]
    public void Matches_DateOtherThanLastModified_ShouldIgnoreRange(int offsetSeconds)
    {
        // Arrange — RFC 9110 §13.1.5 applies the range only on an exact match. Before #1185 a later
        // date applied it, sending a 206 of bytes the client's copy does not splice with.
        HttpIfRange ifRange = HttpIfRange.FromDate(_lastModified.AddSeconds(offsetSeconds));

        // Act
        bool applies = ifRange.Matches(null, _lastModified, _responseDate);

        // Assert
        applies.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: sub-second parts are dropped before comparing")]
    public void Matches_SubSecondParts_ShouldCompareWholeSeconds()
    {
        // Arrange — an HTTP-date carries whole seconds, so a sub-second write is not a modification,
        // and a date built in code with a sub-second part names the second it falls in.
        HttpIfRange ifRange = HttpIfRange.FromDate(_lastModified.AddMilliseconds(250));

        // Act
        bool applies = ifRange.Matches(null, _lastModified.AddMilliseconds(500), _responseDate);

        // Assert
        applies.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: dates are compared as instants, whatever their offset")]
    public void Matches_DateInAnotherOffset_ShouldApplyRange()
    {
        // Arrange
        HttpIfRange ifRange = HttpIfRange.FromDate(_lastModified.ToOffset(TimeSpan.FromHours(5)));

        // Act
        bool applies = ifRange.Matches(null, _lastModified, _responseDate);

        // Assert
        applies.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: a Last-Modified in the response's second, or after it, is weak and ignores the range")]
    [InlineData(0)]     // modified in the second the response is sent: it can change again unseen
    [InlineData(999)]
    [InlineData(-1_000)] // dated after the response (clock skew, a future mtime)
    [InlineData(-60_000)]
    public void Matches_WeakLastModified_ShouldIgnoreRange(int responseOffsetMilliseconds)
    {
        // Arrange — RFC 9110 §8.8.2.2: Last-Modified is strong only when it is at least one second
        // before the response's Date, and §13.1.5 never applies the range on a weak date.
        HttpIfRange ifRange = HttpIfRange.FromDate(_lastModified);

        // Act
        bool applies = ifRange.Matches(null, _lastModified, _lastModified.AddMilliseconds(responseOffsetMilliseconds));

        // Assert
        applies.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: a Last-Modified one second before the response is strong")]
    public void Matches_LastModifiedOneSecondBeforeResponse_ShouldApplyRange()
    {
        // Arrange — the boundary: the Date header would read one second after Last-Modified.
        HttpIfRange ifRange = HttpIfRange.FromDate(_lastModified);

        // Act
        bool applies = ifRange.Matches(null, _lastModified, _lastModified.AddSeconds(1));

        // Assert
        applies.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: a date with no current Last-Modified ignores the range")]
    public void Matches_DateFormButNoLastModified_ShouldIgnoreRange()
    {
        // Arrange
        HttpIfRange ifRange = HttpIfRange.FromDate(_lastModified);

        // Act
        bool applies = ifRange.Matches(HttpEntityTag.Strong("v1"), currentLastModified: null, _responseDate);

        // Assert
        applies.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpIfRange.Matches: a default instance carries no validator and ignores the range")]
    public void Matches_DefaultInstance_ShouldIgnoreRange()
    {
        // Arrange
        HttpIfRange ifRange = default;

        // Act
        bool applies = ifRange.Matches(HttpEntityTag.Strong("v1"), _lastModified, _responseDate);

        // Assert
        applies.ShouldBeFalse();
    }
}
