using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 8.8.3 compliance tests for <see cref="HttpEntityTag"/>: parsing of strong and weak
/// forms, rejection of malformed tags, and the full strong/weak comparison matrix (&#167; 8.8.3.2).
/// </summary>
public class HttpEntityTagTests
{
    // ============================================================================
    // Parsing
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: strong tag parses")]
    public void TryParse_StrongTag_ShouldParse()
    {
        HttpEntityTag.TryParse("\"xyzzy\"", out HttpEntityTag tag).ShouldBeTrue();

        tag.IsWeak.ShouldBeFalse();
        tag.Tag.ShouldBe("xyzzy");
        tag.ToString().ShouldBe("\"xyzzy\"");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: weak tag parses")]
    public void TryParse_WeakTag_ShouldParse()
    {
        HttpEntityTag.TryParse("W/\"xyzzy\"", out HttpEntityTag tag).ShouldBeTrue();

        tag.IsWeak.ShouldBeTrue();
        tag.Tag.ShouldBe("xyzzy");
        tag.ToString().ShouldBe("W/\"xyzzy\"");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: empty opaque tag parses")]
    public void TryParse_EmptyOpaqueTag_ShouldParse()
    {
        HttpEntityTag.TryParse("\"\"", out HttpEntityTag tag).ShouldBeTrue();

        tag.Tag.ShouldBe("");
        tag.IsEmpty.ShouldBeFalse();
        tag.ToString().ShouldBe("\"\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xyzzy")]          // missing quotes
    [InlineData("\"xyzzy")]        // missing closing quote
    [InlineData("xyzzy\"")]        // missing opening quote
    [InlineData("w/\"xyzzy\"")]    // lowercase weakness indicator is not valid (%s"W/")
    [InlineData("W\"xyzzy\"")]     // missing slash
    [InlineData("\"xy\"zy\"")]     // embedded quote
    [InlineData("\"xy zzy\"")]     // embedded space is not an etagc
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        HttpEntityTag.TryParse(raw, out HttpEntityTag tag).ShouldBeFalse();
        tag.IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: Parse throws on malformed")]
    public void Parse_Malformed_ShouldThrowHttpException()
    {
        Should.Throw<HttpException>(() => HttpEntityTag.Parse("not-a-tag"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: leading/trailing whitespace tolerated")]
    public void TryParse_SurroundingWhitespace_ShouldTrim()
    {
        HttpEntityTag.TryParse("  W/\"abc\"  ", out HttpEntityTag tag).ShouldBeTrue();

        tag.IsWeak.ShouldBeTrue();
        tag.Tag.ShouldBe("abc");
    }

    // ============================================================================
    // Comparison matrix (RFC 9110 §8.8.3.2)
    // ============================================================================
    //
    //   ETag 1     ETag 2     Strong   Weak
    //   "1"        "1"        match    match
    //   W/"1"      "1"        no       match
    //   "1"        W/"1"      no       match
    //   W/"1"      W/"1"      no       match
    //   "1"        "2"        no       no

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: strong vs strong same content")]
    public void Compare_StrongStrong_SameContent_ShouldMatchBoth()
    {
        HttpEntityTag a = HttpEntityTag.Strong("1");
        HttpEntityTag b = HttpEntityTag.Strong("1");

        a.StrongEquals(b).ShouldBeTrue();
        a.WeakEquals(b).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: weak vs strong same content")]
    public void Compare_WeakStrong_SameContent_ShouldMatchWeakOnly()
    {
        HttpEntityTag weak = HttpEntityTag.Weak("1");
        HttpEntityTag strong = HttpEntityTag.Strong("1");

        weak.StrongEquals(strong).ShouldBeFalse();
        weak.WeakEquals(strong).ShouldBeTrue();
        strong.StrongEquals(weak).ShouldBeFalse();
        strong.WeakEquals(weak).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: weak vs weak same content")]
    public void Compare_WeakWeak_SameContent_ShouldMatchWeakOnly()
    {
        HttpEntityTag a = HttpEntityTag.Weak("1");
        HttpEntityTag b = HttpEntityTag.Weak("1");

        a.StrongEquals(b).ShouldBeFalse();
        a.WeakEquals(b).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: differing content never matches")]
    public void Compare_DifferentContent_ShouldMatchNeither()
    {
        HttpEntityTag a = HttpEntityTag.Strong("1");
        HttpEntityTag b = HttpEntityTag.Strong("2");

        a.StrongEquals(b).ShouldBeFalse();
        a.WeakEquals(b).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: comparison is case-sensitive")]
    public void Compare_DifferentCase_ShouldNotMatch()
    {
        HttpEntityTag lower = HttpEntityTag.Strong("abc");
        HttpEntityTag upper = HttpEntityTag.Strong("ABC");

        lower.StrongEquals(upper).ShouldBeFalse();
        lower.WeakEquals(upper).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: structural equality distinguishes weakness")]
    public void Equals_DiffersOnWeakness()
    {
        HttpEntityTag weak = HttpEntityTag.Weak("1");
        HttpEntityTag strong = HttpEntityTag.Strong("1");

        (weak == strong).ShouldBeFalse();
        weak.Equals(HttpEntityTag.Weak("1")).ShouldBeTrue();
        weak.GetHashCode().ShouldBe(HttpEntityTag.Weak("1").GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpEntityTag: factory rejects invalid content")]
    public void Strong_InvalidContent_ShouldThrow()
    {
        Should.Throw<HttpException>(() => HttpEntityTag.Strong("has\"quote"));
    }
}
