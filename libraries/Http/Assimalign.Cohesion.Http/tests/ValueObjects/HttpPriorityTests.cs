using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9218 compliance tests for <see cref="HttpPriority"/>: parsing of the
/// <c>u</c>/<c>i</c> Priority Field Value dictionary, the default urgency/
/// incremental fallbacks, tolerance of absent, out-of-range, wrong-typed, and
/// unknown members, and canonical serialization.
/// </summary>
public class HttpPriorityTests
{
    // ============================================================================
    // Parsing — valid values
    // ============================================================================

    [Theory]
    [InlineData("u=0", 0, false)]
    [InlineData("u=7", 7, false)]
    [InlineData("u=2, i", 2, true)]
    [InlineData("u=5, i=?1", 5, true)]
    [InlineData("u=5, i=?0", 5, false)]
    [InlineData("i", 3, true)]              // urgency absent → default 3, incremental set
    [InlineData("i=?1", 3, true)]
    [InlineData("", 3, false)]              // empty dictionary → all defaults
    [InlineData("u=1, i, x=9", 1, true)]    // unknown member ignored
    public void TryParse_ValidValue_ShouldYieldUrgencyAndIncremental(string raw, int expectedUrgency, bool expectedIncremental)
    {
        bool ok = HttpPriority.TryParse(raw, out HttpPriority priority);

        ok.ShouldBeTrue();
        priority.Urgency.ShouldBe(expectedUrgency);
        priority.Incremental.ShouldBe(expectedIncremental);
    }

    // ============================================================================
    // Parsing — RFC 9218 §4 tolerance (well-formed dictionary, ignored members)
    // ============================================================================

    [Theory]
    [InlineData("u=8")]      // out of range (> 7) → urgency ignored → default 3
    [InlineData("u=-1")]     // out of range (< 0)
    [InlineData("u=abc")]    // wrong type (token, not integer)
    [InlineData("u=1.5")]    // wrong type (decimal)
    public void TryParse_InvalidUrgencyMember_ShouldFallBackToDefaultUrgency(string raw)
    {
        bool ok = HttpPriority.TryParse(raw, out HttpPriority priority);

        ok.ShouldBeTrue();
        priority.Urgency.ShouldBe(HttpPriority.DefaultUrgency);
        priority.Incremental.ShouldBeFalse();
    }

    [Theory]
    [InlineData("i=3")]      // wrong type (integer, not boolean) → incremental ignored
    [InlineData("i=x")]      // wrong type (token)
    public void TryParse_InvalidIncrementalMember_ShouldFallBackToNonIncremental(string raw)
    {
        bool ok = HttpPriority.TryParse(raw, out HttpPriority priority);

        ok.ShouldBeTrue();
        priority.Incremental.ShouldBeFalse();
    }

    // ============================================================================
    // Parsing — malformed dictionary as a whole
    // ============================================================================

    [Theory]
    [InlineData("u=\"unterminated")]   // string with no closing quote
    [InlineData("U=2")]                // key must be lowercase per RFC 9651
    [InlineData("u=2;")]               // dangling parameter separator
    public void TryParse_MalformedDictionary_ShouldFailAndYieldDefault(string raw)
    {
        bool ok = HttpPriority.TryParse(raw, out HttpPriority priority);

        ok.ShouldBeFalse();
        priority.ShouldBe(HttpPriority.Default);
    }

    // ============================================================================
    // Defaults + construction
    // ============================================================================

    [Fact]
    public void Default_ShouldBeUrgencyThreeNonIncremental()
    {
        HttpPriority.Default.Urgency.ShouldBe(3);
        HttpPriority.Default.Incremental.ShouldBeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    [InlineData(int.MaxValue)]
    public void Constructor_UrgencyOutOfRange_ShouldThrow(int urgency)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpPriority(urgency, false));
    }

    // ============================================================================
    // Serialization + round-trip
    // ============================================================================

    [Theory]
    [InlineData(3, false, "")]          // default → empty (both members omitted)
    [InlineData(2, false, "u=2")]
    [InlineData(3, true, "i")]          // default urgency omitted, bare boolean flag
    [InlineData(1, true, "u=1, i")]
    [InlineData(0, false, "u=0")]
    public void Serialize_ShouldOmitDefaultMembers(int urgency, bool incremental, string expected)
    {
        new HttpPriority(urgency, incremental).Serialize().ShouldBe(expected);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, false)]
    [InlineData(5, true)]
    [InlineData(7, false)]
    public void Serialize_ThenParse_ShouldRoundTrip(int urgency, bool incremental)
    {
        HttpPriority original = new(urgency, incremental);

        HttpPriority.TryParse(original.Serialize(), out HttpPriority parsed).ShouldBeTrue();

        parsed.ShouldBe(original);
    }

    // ============================================================================
    // Equality
    // ============================================================================

    [Fact]
    public void Equality_SameUrgencyAndIncremental_ShouldBeEqual()
    {
        (new HttpPriority(4, true) == new HttpPriority(4, true)).ShouldBeTrue();
        (new HttpPriority(4, true) != new HttpPriority(4, false)).ShouldBeTrue();
        new HttpPriority(2, false).GetHashCode().ShouldBe(new HttpPriority(2, false).GetHashCode());
    }

    // ============================================================================
    // Header key
    // ============================================================================

    [Fact]
    public void PriorityHeaderKey_ShouldBeCanonicalName()
    {
        HttpHeaderKey.Priority.Value.ShouldBe("Priority");
    }
}
