using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 12.5 compliance tests for <see cref="HttpAcceptParser"/>: quality-value
/// parsing, preference ordering (specificity breaking q ties), and tolerance of malformed
/// segments across the <c>Accept</c> family of headers.
/// </summary>
public class HttpAcceptParserTests
{
    // ============================================================================
    // Accept
    // ============================================================================

    [Fact]
    public void ParseAccept_NoQuality_ShouldDefaultToOne()
    {
        IReadOnlyList<HttpMediaTypeQuality> result = HttpAcceptParser.ParseAccept("text/html, application/json");

        result.Count.ShouldBe(2);
        result[0].Quality.ShouldBe(HttpQuality.One);
        result[1].Quality.ShouldBe(HttpQuality.One);
    }

    [Fact]
    public void ParseAccept_ShouldOrderByQualityDescending()
    {
        IReadOnlyList<HttpMediaTypeQuality> result =
            HttpAcceptParser.ParseAccept("text/plain;q=0.5, application/json;q=0.9, text/html;q=0.8");

        result[0].MediaType.ShouldBe(HttpMediaType.ApplicationJson);
        result[1].MediaType.ShouldBe(HttpMediaType.TextHtml);
        result[2].MediaType.ShouldBe(HttpMediaType.TextPlain);
    }

    [Fact]
    public void ParseAccept_EqualQuality_MoreSpecificFirst()
    {
        IReadOnlyList<HttpMediaTypeQuality> result =
            HttpAcceptParser.ParseAccept("text/*;q=0.5, text/html;q=0.5, */*;q=0.5");

        result[0].MediaType.ShouldBe(HttpMediaType.TextHtml);   // specificity 2
        result[1].MediaType.Type.ShouldBe("text");              // text/* specificity 1
        result[1].MediaType.IsWildcardSubType.ShouldBeTrue();
        result[2].MediaType.ShouldBe(HttpMediaType.Any);        // */* specificity 0
    }

    [Fact]
    public void ParseAccept_ShouldRetainMediaTypeParametersBeforeQ()
    {
        IReadOnlyList<HttpMediaTypeQuality> result = HttpAcceptParser.ParseAccept("text/html;level=1;q=0.8");

        result.Count.ShouldBe(1);
        result[0].Quality.ShouldBe(HttpQuality.FromPerMille(800));
        result[0].MediaType.TryGetParameter("level", out string? level).ShouldBeTrue();
        level.ShouldBe("1");
    }

    [Fact]
    public void ParseAccept_ShouldIgnoreAcceptExtensionsAfterQ()
    {
        IReadOnlyList<HttpMediaTypeQuality> result = HttpAcceptParser.ParseAccept("text/html;q=0.8;ext=ignored");

        result.Count.ShouldBe(1);
        result[0].Quality.ShouldBe(HttpQuality.FromPerMille(800));
        result[0].MediaType.TryGetParameter("ext", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseAccept_NullOrBlank_ShouldReturnEmpty(string? raw)
    {
        HttpAcceptParser.ParseAccept(raw).Count.ShouldBe(0);
    }

    [Fact]
    public void ParseAccept_MalformedSegments_ShouldBeSkipped()
    {
        IReadOnlyList<HttpMediaTypeQuality> result =
            HttpAcceptParser.ParseAccept("text/html, , garbage/, application/json;q=notanumber, text/plain");

        result.Count.ShouldBe(2);
        result.ShouldContain(entry => entry.MediaType == HttpMediaType.TextHtml);
        result.ShouldContain(entry => entry.MediaType == HttpMediaType.TextPlain);
    }

    [Fact]
    public void ParseAccept_QuotedCommaInParameter_ShouldNotSplitEntry()
    {
        IReadOnlyList<HttpMediaTypeQuality> result =
            HttpAcceptParser.ParseAccept("text/plain;foo=\"a,b\", application/json");

        result.Count.ShouldBe(2);
        result.ShouldContain(entry => entry.MediaType.Type == "text");
        result.ShouldContain(entry => entry.MediaType == HttpMediaType.ApplicationJson);
    }

    // ============================================================================
    // Accept-Encoding / Charset / Language (token lists)
    // ============================================================================

    [Fact]
    public void ParseAcceptEncoding_ShouldPreserveOrderForEqualQuality()
    {
        IReadOnlyList<HttpQualityValue> result = HttpAcceptParser.ParseAcceptEncoding("gzip, br, deflate");

        result.Count.ShouldBe(3);
        result[0].Value.ShouldBe("gzip");
        result[1].Value.ShouldBe("br");
        result[2].Value.ShouldBe("deflate");
    }

    [Fact]
    public void ParseAcceptEncoding_ShouldOrderByQualityAndKeepWildcard()
    {
        IReadOnlyList<HttpQualityValue> result =
            HttpAcceptParser.ParseAcceptEncoding("gzip;q=1.0, identity;q=0.5, *;q=0");

        result[0].Value.ShouldBe("gzip");
        result[1].Value.ShouldBe("identity");
        result[2].Value.ShouldBe("*");
        result[2].Quality.ShouldBe(HttpQuality.Zero);
        result[2].IsWildcard.ShouldBeTrue();
    }

    [Fact]
    public void ParseAcceptEncoding_MalformedQuality_ShouldSkipEntry()
    {
        IReadOnlyList<HttpQualityValue> result = HttpAcceptParser.ParseAcceptEncoding("gzip;q=bad, br");

        result.Count.ShouldBe(1);
        result[0].Value.ShouldBe("br");
    }

    [Fact]
    public void ParseAcceptLanguage_ShouldParseRangesAndWeights()
    {
        IReadOnlyList<HttpQualityValue> result =
            HttpAcceptParser.ParseAcceptLanguage("en-US, en;q=0.8, fr;q=0.5");

        result[0].Value.ShouldBe("en-US");
        result[0].Quality.ShouldBe(HttpQuality.One);
        result[1].Value.ShouldBe("en");
        result[1].Quality.ShouldBe(HttpQuality.FromPerMille(800));
        result[2].Value.ShouldBe("fr");
    }

    [Fact]
    public void ParseAcceptCharset_ShouldParseWeights()
    {
        IReadOnlyList<HttpQualityValue> result = HttpAcceptParser.ParseAcceptCharset("utf-8, iso-8859-1;q=0.5");

        result[0].Value.ShouldBe("utf-8");
        result[1].Value.ShouldBe("iso-8859-1");
        result[1].Quality.ShouldBe(HttpQuality.FromPerMille(500));
    }

    [Fact]
    public void ParseTokenList_EqualQuality_ExplicitBeatsWildcard()
    {
        IReadOnlyList<HttpQualityValue> result = HttpAcceptParser.ParseAcceptLanguage("*;q=0.5, en;q=0.5");

        result[0].Value.ShouldBe("en");
        result[1].Value.ShouldBe("*");
    }
}
