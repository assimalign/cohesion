using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 5.6.7 compliance tests for <see cref="HttpDate"/>: parsing of the three accepted
/// HTTP-date forms (IMF-fixdate, RFC 850, asctime), rejection of malformed dates (which callers
/// treat as absent per &#167; 13.1.3), and IMF-fixdate formatting.
/// </summary>
public class HttpDateTests
{
    private static readonly DateTimeOffset Reference = new(1994, 11, 6, 8, 49, 37, TimeSpan.Zero);

    [Theory]
    [InlineData("Sun, 06 Nov 1994 08:49:37 GMT")]     // IMF-fixdate (preferred)
    [InlineData("Sunday, 06-Nov-94 08:49:37 GMT")]    // RFC 850 (obsolete)
    [InlineData("Sun Nov  6 08:49:37 1994")]          // asctime (obsolete), space-padded day
    public void TryParse_AllThreeForms_ShouldYieldSameInstant(string raw)
    {
        bool ok = HttpDate.TryParse(raw, out DateTimeOffset date);

        ok.ShouldBeTrue();
        date.ShouldBe(Reference);
        date.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpDate: asctime double-digit day parses")]
    public void TryParse_AsctimeDoubleDigitDay_ShouldParse()
    {
        // 20 Nov 1994 was a Sunday; ParseExact validates the weekday against the date.
        bool ok = HttpDate.TryParse("Sun Nov 20 08:49:37 1994", out DateTimeOffset date);

        ok.ShouldBeTrue();
        date.ShouldBe(new DateTimeOffset(1994, 11, 20, 8, 49, 37, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    [InlineData("Sun, 06 Nov 1994 08:49:37")]         // missing GMT
    [InlineData("2014-02-03T12:00:00Z")]              // ISO 8601 is not an HTTP-date
    [InlineData("Xxx, 32 Zzz 1994 25:99:99 GMT")]     // structurally date-shaped but invalid
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        bool ok = HttpDate.TryParse(raw, out DateTimeOffset date);

        ok.ShouldBeFalse();
        date.ShouldBe(default);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpDate: null input fails")]
    public void TryParse_Null_ShouldFail()
    {
        HttpDate.TryParse((string?)null, out DateTimeOffset date).ShouldBeFalse();
        date.ShouldBe(default);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpDate: Format emits IMF-fixdate")]
    public void Format_ShouldEmitImfFixdate()
    {
        string formatted = HttpDate.Format(Reference);

        formatted.ShouldBe("Sun, 06 Nov 1994 08:49:37 GMT");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpDate: Format converts to UTC")]
    public void Format_NonUtcOffset_ShouldConvertToGmt()
    {
        var eastern = new DateTimeOffset(1994, 11, 6, 3, 49, 37, TimeSpan.FromHours(-5));

        HttpDate.Format(eastern).ShouldBe("Sun, 06 Nov 1994 08:49:37 GMT");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpDate: Format then parse round-trips")]
    public void Format_ThenParse_ShouldRoundTrip()
    {
        string formatted = HttpDate.Format(Reference);

        HttpDate.TryParse(formatted, out DateTimeOffset parsed).ShouldBeTrue();
        parsed.ShouldBe(Reference);
    }
}
