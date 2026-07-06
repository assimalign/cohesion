using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 8.3.1 / &#167; 12.5.1 compliance tests for <see cref="HttpMediaType"/>: parsing
/// of type/subtype, structured-syntax suffixes and parameters, wildcard media ranges, specificity
/// ordering, and directional range matching.
/// </summary>
public class HttpMediaTypeTests
{
    // ============================================================================
    // Parsing — type / subtype
    // ============================================================================

    [Theory]
    [InlineData("text/html", "text", "html")]
    [InlineData("application/json", "application", "json")]
    [InlineData("APPLICATION/JSON", "application", "json")]
    [InlineData("Text/HTML", "text", "html")]
    [InlineData("  text/plain  ", "text", "plain")]
    [InlineData("*/*", "*", "*")]
    [InlineData("text/*", "text", "*")]
    public void TryParse_ValidTypeSubtype_ShouldSplit(string raw, string expectedType, string expectedSubType)
    {
        bool ok = HttpMediaType.TryParse(raw, out HttpMediaType mediaType);

        ok.ShouldBeTrue();
        mediaType.Type.ShouldBe(expectedType);
        mediaType.SubType.ShouldBe(expectedSubType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("text")]
    [InlineData("text/")]
    [InlineData("/plain")]
    [InlineData("/")]
    [InlineData("text plain")]
    [InlineData("*/json")]
    [InlineData("te xt/html")]
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        bool ok = HttpMediaType.TryParse(raw, out HttpMediaType mediaType);

        ok.ShouldBeFalse();
        mediaType.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_Null_ShouldFail()
    {
        HttpMediaType.TryParse(null, out HttpMediaType mediaType).ShouldBeFalse();
        mediaType.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Parse_Invalid_ShouldThrowHttpException()
    {
        Should.Throw<HttpException>(() => HttpMediaType.Parse("not-a-media-type"));
    }

    // ============================================================================
    // Structured-syntax suffix (+json, +xml)
    // ============================================================================

    [Theory]
    [InlineData("application/vnd.api+json", "vnd.api+json", "json")]
    [InlineData("image/svg+xml", "svg+xml", "xml")]
    [InlineData("application/json", "json", "")]
    [InlineData("application/ld+json", "ld+json", "json")]
    public void TryParse_Suffix_ShouldExtract(string raw, string expectedSubType, string expectedSuffix)
    {
        HttpMediaType.TryParse(raw, out HttpMediaType mediaType).ShouldBeTrue();

        mediaType.SubType.ShouldBe(expectedSubType);
        mediaType.Suffix.ShouldBe(expectedSuffix);
    }

    // ============================================================================
    // Parameters
    // ============================================================================

    [Fact]
    public void TryParse_Charset_ShouldExposeParameter()
    {
        HttpMediaType.TryParse("text/html; charset=utf-8", out HttpMediaType mediaType).ShouldBeTrue();

        mediaType.Charset.ShouldBe("utf-8");
        mediaType.Parameters.Count.ShouldBe(1);
        mediaType.TryGetParameter("CHARSET", out string? value).ShouldBeTrue();
        value.ShouldBe("utf-8");
    }

    [Fact]
    public void TryParse_MultipleParameters_ShouldRetainAll()
    {
        HttpMediaType.TryParse("multipart/form-data; boundary=abc123; charset=utf-8", out HttpMediaType mediaType)
            .ShouldBeTrue();

        mediaType.Parameters.Count.ShouldBe(2);
        mediaType.TryGetParameter("boundary", out string? boundary).ShouldBeTrue();
        boundary.ShouldBe("abc123");
        mediaType.Charset.ShouldBe("utf-8");
    }

    [Fact]
    public void TryParse_QuotedParameterValue_ShouldUnquote()
    {
        HttpMediaType.TryParse("text/plain; charset=\"utf-8\"; foo=\"a;b,c\"", out HttpMediaType mediaType)
            .ShouldBeTrue();

        mediaType.Charset.ShouldBe("utf-8");
        mediaType.TryGetParameter("foo", out string? foo).ShouldBeTrue();
        foo.ShouldBe("a;b,c");
    }

    [Fact]
    public void TryParse_MalformedParameter_ShouldBeSkipped()
    {
        HttpMediaType.TryParse("text/html; ; charset=utf-8; broken", out HttpMediaType mediaType).ShouldBeTrue();

        mediaType.Charset.ShouldBe("utf-8");
        mediaType.Parameters.Count.ShouldBe(1);
    }

    // ============================================================================
    // Wildcards & specificity (RFC 9110 §12.5.1)
    // ============================================================================

    [Theory]
    [InlineData("*/*", 0)]
    [InlineData("text/*", 1)]
    [InlineData("text/plain", 2)]
    [InlineData("text/plain; charset=utf-8", 3)]
    public void Specificity_ShouldRankRanges(string raw, int expected)
    {
        HttpMediaType.TryParse(raw, out HttpMediaType mediaType).ShouldBeTrue();

        mediaType.Specificity.ShouldBe(expected);
    }

    [Fact]
    public void Wildcards_ShouldReportFlags()
    {
        HttpMediaType.Any.IsWildcardType.ShouldBeTrue();
        HttpMediaType.Any.IsWildcardSubType.ShouldBeTrue();
        HttpMediaType.Any.HasWildcard.ShouldBeTrue();

        HttpMediaType.TryParse("text/*", out HttpMediaType textAny).ShouldBeTrue();
        textAny.IsWildcardType.ShouldBeFalse();
        textAny.IsWildcardSubType.ShouldBeTrue();
        textAny.HasWildcard.ShouldBeTrue();

        HttpMediaType.TextHtml.HasWildcard.ShouldBeFalse();
    }

    // ============================================================================
    // Matching (Includes)
    // ============================================================================

    [Theory]
    [InlineData("*/*", "text/html", true)]
    [InlineData("text/*", "text/html", true)]
    [InlineData("text/*", "image/png", false)]
    [InlineData("text/html", "text/html", true)]
    [InlineData("text/html", "text/plain", false)]
    [InlineData("TEXT/HTML", "text/html", true)]
    public void Includes_TypeAndSubtype_ShouldRespectWildcards(string range, string candidate, bool expected)
    {
        HttpMediaType.TryParse(range, out HttpMediaType rangeType).ShouldBeTrue();
        HttpMediaType.TryParse(candidate, out HttpMediaType candidateType).ShouldBeTrue();

        rangeType.Includes(candidateType).ShouldBe(expected);
    }

    [Fact]
    public void Includes_RangeParameters_MustBeMatchedByCandidate()
    {
        HttpMediaType.TryParse("text/html; charset=utf-8", out HttpMediaType range).ShouldBeTrue();
        HttpMediaType.TryParse("text/html; charset=utf-8", out HttpMediaType exact).ShouldBeTrue();
        HttpMediaType.TryParse("text/html", out HttpMediaType noCharset).ShouldBeTrue();
        HttpMediaType.TryParse("text/html; charset=iso-8859-1", out HttpMediaType otherCharset).ShouldBeTrue();

        range.Includes(exact).ShouldBeTrue();
        range.Includes(noCharset).ShouldBeFalse();
        range.Includes(otherCharset).ShouldBeFalse();
    }

    [Fact]
    public void Includes_CandidateMayCarryExtraParameters()
    {
        HttpMediaType.TryParse("text/html", out HttpMediaType range).ShouldBeTrue();
        HttpMediaType.TryParse("text/html; charset=utf-8", out HttpMediaType candidate).ShouldBeTrue();

        range.Includes(candidate).ShouldBeTrue();
    }

    // ============================================================================
    // Equality & round-trip
    // ============================================================================

    [Fact]
    public void Equality_ShouldBeCaseInsensitiveAndParameterOrderInsensitive()
    {
        HttpMediaType.TryParse("Text/HTML; charset=UTF-8; foo=bar", out HttpMediaType a).ShouldBeTrue();
        HttpMediaType.TryParse("text/html; foo=bar; charset=utf-8", out HttpMediaType b).ShouldBeTrue();

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentParameters_ShouldNotBeEqual()
    {
        HttpMediaType.TryParse("text/html; charset=utf-8", out HttpMediaType a).ShouldBeTrue();
        HttpMediaType.TryParse("text/html", out HttpMediaType b).ShouldBeTrue();

        a.ShouldNotBe(b);
        (a != b).ShouldBeTrue();
    }

    [Fact]
    public void ToString_ShouldRoundTripThroughParse()
    {
        HttpMediaType.TryParse("application/vnd.api+json; charset=utf-8", out HttpMediaType original).ShouldBeTrue();

        string rendered = original.ToString();
        HttpMediaType.TryParse(rendered, out HttpMediaType reparsed).ShouldBeTrue();

        reparsed.ShouldBe(original);
        rendered.ShouldBe("application/vnd.api+json; charset=utf-8");
    }

    [Fact]
    public void ToString_ShouldQuoteValuesWithDelimiters()
    {
        HttpMediaType.TryParse("text/plain; foo=\"a;b\"", out HttpMediaType mediaType).ShouldBeTrue();

        mediaType.ToString().ShouldBe("text/plain; foo=\"a;b\"");
    }

    [Fact]
    public void WellKnown_ShouldMatchCanonicalText()
    {
        HttpMediaType.ApplicationJson.ToString().ShouldBe("application/json");
        HttpMediaType.TextHtml.ToString().ShouldBe("text/html");
        HttpMediaType.Any.ToString().ShouldBe("*/*");
        HttpMediaType.MultipartFormData.ToString().ShouldBe("multipart/form-data");
    }
}
