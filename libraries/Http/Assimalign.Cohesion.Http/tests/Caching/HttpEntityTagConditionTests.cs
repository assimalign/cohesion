using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 13.1.1 / &#167; 13.1.2 compliance tests for <see cref="HttpEntityTagCondition"/>:
/// parsing of the <c>*</c> wildcard and comma-separated multi-tag lists, and strong/weak matching
/// used by <c>If-Match</c> and <c>If-None-Match</c>.
/// </summary>
public class HttpEntityTagConditionTests
{
    // ============================================================================
    // Parsing
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: wildcard parses")]
    public void TryParse_Wildcard_ShouldSetIsAny()
    {
        HttpEntityTagCondition.TryParse("*", out HttpEntityTagCondition condition).ShouldBeTrue();

        condition.IsAny.ShouldBeTrue();
        condition.Tags.Count.ShouldBe(0);
        condition.ToString().ShouldBe("*");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: single tag parses")]
    public void TryParse_SingleTag_ShouldParse()
    {
        HttpEntityTagCondition.TryParse("\"a\"", out HttpEntityTagCondition condition).ShouldBeTrue();

        condition.IsAny.ShouldBeFalse();
        condition.Tags.Count.ShouldBe(1);
        condition.Tags[0].Tag.ShouldBe("a");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: multi-tag list parses")]
    public void TryParse_MultiTagList_ShouldParseAll()
    {
        HttpEntityTagCondition.TryParse("\"a\", W/\"b\" , \"c\"", out HttpEntityTagCondition condition).ShouldBeTrue();

        condition.Tags.Count.ShouldBe(3);
        condition.Tags[0].Tag.ShouldBe("a");
        condition.Tags[0].IsWeak.ShouldBeFalse();
        condition.Tags[1].Tag.ShouldBe("b");
        condition.Tags[1].IsWeak.ShouldBeTrue();
        condition.Tags[2].Tag.ShouldBe("c");
        condition.ToString().ShouldBe("\"a\", W/\"b\", \"c\"");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: quoted comma is not a delimiter")]
    public void TryParse_QuotedComma_ShouldNotSplit()
    {
        HttpEntityTagCondition.TryParse("\"a,b\"", out HttpEntityTagCondition condition).ShouldBeTrue();

        condition.Tags.Count.ShouldBe(1);
        condition.Tags[0].Tag.ShouldBe("a,b");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: empty list elements ignored")]
    public void TryParse_EmptyElements_ShouldBeIgnored()
    {
        HttpEntityTagCondition.TryParse("\"a\", , \"b\"", out HttpEntityTagCondition condition).ShouldBeTrue();

        condition.Tags.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]                 // only empty elements
    [InlineData("\"a\", bad")]        // a malformed member fails the whole list
    [InlineData("*, \"a\"")]          // wildcard must appear alone
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        HttpEntityTagCondition.TryParse(raw, out HttpEntityTagCondition condition).ShouldBeFalse();
        condition.IsAny.ShouldBeFalse();
        condition.Tags.Count.ShouldBe(0);
    }

    // ============================================================================
    // Matching
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: If-Match uses strong comparison")]
    public void MatchesStrong_WeakCurrentTag_ShouldNotMatch()
    {
        HttpEntityTagCondition condition = HttpEntityTagCondition.Parse("W/\"1\"");

        // Strong comparison: a weak listed tag can never strongly match.
        condition.MatchesStrong(HttpEntityTag.Weak("1"), hasCurrentRepresentation: true).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: If-Match strong match")]
    public void MatchesStrong_StrongPair_ShouldMatch()
    {
        HttpEntityTagCondition condition = HttpEntityTagCondition.Parse("\"1\", \"2\"");

        condition.MatchesStrong(HttpEntityTag.Strong("2"), hasCurrentRepresentation: true).ShouldBeTrue();
        condition.MatchesStrong(HttpEntityTag.Strong("3"), hasCurrentRepresentation: true).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: If-None-Match uses weak comparison")]
    public void MatchesWeak_WeakPair_ShouldMatch()
    {
        HttpEntityTagCondition condition = HttpEntityTagCondition.Parse("W/\"1\"");

        condition.MatchesWeak(HttpEntityTag.Strong("1"), hasCurrentRepresentation: true).ShouldBeTrue();
        condition.MatchesWeak(HttpEntityTag.Weak("1"), hasCurrentRepresentation: true).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: wildcard matches when representation exists")]
    public void Matches_Wildcard_DependsOnRepresentation()
    {
        HttpEntityTagCondition condition = HttpEntityTagCondition.Parse("*");

        condition.MatchesStrong(current: null, hasCurrentRepresentation: true).ShouldBeTrue();
        condition.MatchesWeak(current: null, hasCurrentRepresentation: true).ShouldBeTrue();
        condition.MatchesStrong(current: null, hasCurrentRepresentation: false).ShouldBeFalse();
        condition.MatchesWeak(current: null, hasCurrentRepresentation: false).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTagCondition: no current tag never matches a list")]
    public void Matches_NullCurrentTag_ShouldNotMatchList()
    {
        HttpEntityTagCondition condition = HttpEntityTagCondition.Parse("\"1\"");

        condition.MatchesStrong(current: null, hasCurrentRepresentation: true).ShouldBeFalse();
        condition.MatchesWeak(current: null, hasCurrentRepresentation: true).ShouldBeFalse();
    }
}
