using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9110 &#167; 12.5 server-driven negotiation tests for <see cref="HttpContentNegotiation"/>:
/// media-type selection (including the &#167; 12.5.1 worked example), token selection, and the
/// &#167; 12.5.3 identity rules for content-coding selection.
/// </summary>
public class HttpContentNegotiationTests
{
    private static readonly IReadOnlyList<HttpMediaType> JsonThenXml = new[]
    {
        HttpMediaType.ApplicationJson,
        HttpMediaType.ApplicationXml,
    };

    // ============================================================================
    // Media-type negotiation
    // ============================================================================

    [Fact]
    public void NegotiateMediaType_NoAccept_ShouldReturnServerPreferred()
    {
        HttpContentNegotiation.TryNegotiateMediaType((string?)null, JsonThenXml, out HttpMediaType selected)
            .ShouldBeTrue();

        selected.ShouldBe(HttpMediaType.ApplicationJson);
    }

    [Fact]
    public void NegotiateMediaType_ExactMatch_ShouldSelectIt()
    {
        HttpContentNegotiation.TryNegotiateMediaType("application/xml", JsonThenXml, out HttpMediaType selected)
            .ShouldBeTrue();

        selected.ShouldBe(HttpMediaType.ApplicationXml);
    }

    [Fact]
    public void NegotiateMediaType_Wildcard_ShouldSkipUnacceptableServerOptions()
    {
        var server = new[] { HttpMediaType.ApplicationJson, HttpMediaType.TextHtml };

        HttpContentNegotiation.TryNegotiateMediaType("text/*", server, out HttpMediaType selected).ShouldBeTrue();

        selected.ShouldBe(HttpMediaType.TextHtml);
    }

    [Fact]
    public void NegotiateMediaType_NothingAcceptable_ShouldReturnFalse()
    {
        var server = new[] { HttpMediaType.TextHtml };

        HttpContentNegotiation.TryNegotiateMediaType("application/json", server, out HttpMediaType selected)
            .ShouldBeFalse();

        selected.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void NegotiateMediaType_QualityZero_ShouldExcludeRepresentation()
    {
        var server = new[] { HttpMediaType.TextHtml, HttpMediaType.ApplicationJson };

        HttpContentNegotiation.TryNegotiateMediaType("text/html;q=0, */*;q=0.5", server, out HttpMediaType selected)
            .ShouldBeTrue();

        selected.ShouldBe(HttpMediaType.ApplicationJson);
    }

    [Fact]
    public void NegotiateMediaType_ServerPreferenceBreaksQualityTies()
    {
        var server = new[] { HttpMediaType.ApplicationJson, HttpMediaType.TextHtml };

        HttpContentNegotiation.TryNegotiateMediaType("*/*", server, out HttpMediaType selected).ShouldBeTrue();

        selected.ShouldBe(HttpMediaType.ApplicationJson);
    }

    // RFC 9110 §12.5.1 worked example.
    private const string RfcExampleAccept =
        "text/*;q=0.3, text/html;q=0.7, text/html;level=1, text/html;level=2;q=0.4, */*;q=0.5";

    [Theory]
    [InlineData("text/html", "text/plain", "text/html")]     // 0.7 vs 0.3
    [InlineData("image/jpeg", "text/plain", "image/jpeg")]   // 0.5 (*/*) vs 0.3 (text/*)
    [InlineData("text/html;level=2", "text/plain", "text/html;level=2")] // 0.4 vs 0.3
    public void NegotiateMediaType_RfcExample_ShouldPickHigherQuality(
        string firstOption, string secondOption, string expected)
    {
        var server = new[] { new HttpMediaType(firstOption), new HttpMediaType(secondOption) };

        HttpContentNegotiation.TryNegotiateMediaType(RfcExampleAccept, server, out HttpMediaType selected)
            .ShouldBeTrue();

        selected.ShouldBe(new HttpMediaType(expected));
    }

    [Fact]
    public void NegotiateMediaType_RfcExample_MostSpecificRangeAssignsQuality()
    {
        // text/html;level=1 is matched exactly (q=1.0), beating the plain text/html range (q=0.7).
        var server = new[] { HttpMediaType.TextHtml, new HttpMediaType("text/html;level=1") };

        HttpContentNegotiation.TryNegotiateMediaType(RfcExampleAccept, server, out HttpMediaType selected)
            .ShouldBeTrue();

        selected.ShouldBe(new HttpMediaType("text/html;level=1"));
    }

    // ============================================================================
    // Token negotiation (charset / language)
    // ============================================================================

    [Fact]
    public void SelectByQuality_ShouldPreferHigherQualityRegardlessOfServerOrder()
    {
        IReadOnlyList<HttpQualityValue> accepted = HttpAcceptParser.ParseAcceptLanguage("en, fr;q=0.5");

        HttpContentNegotiation.TrySelectByQuality(accepted, new[] { "fr", "en" }, out string selected).ShouldBeTrue();

        selected.ShouldBe("en");
    }

    [Fact]
    public void SelectByQuality_NoPreference_ShouldReturnServerFirst()
    {
        HttpContentNegotiation.TrySelectByQuality(
            System.Array.Empty<HttpQualityValue>(), new[] { "en", "fr" }, out string selected).ShouldBeTrue();

        selected.ShouldBe("en");
    }

    [Fact]
    public void SelectByQuality_Unlisted_ShouldNotBeAcceptable()
    {
        IReadOnlyList<HttpQualityValue> accepted = HttpAcceptParser.ParseAcceptLanguage("de");

        HttpContentNegotiation.TrySelectByQuality(accepted, new[] { "en" }, out string selected).ShouldBeFalse();
        selected.ShouldBe(string.Empty);
    }

    [Fact]
    public void SelectByQuality_ExactMatchBeatsWildcard()
    {
        IReadOnlyList<HttpQualityValue> accepted = HttpAcceptParser.ParseAcceptLanguage("en;q=0.2, *;q=0.9");

        HttpContentNegotiation.TrySelectByQuality(accepted, new[] { "en", "fr" }, out string selected).ShouldBeTrue();

        // fr resolves through '*' (0.9); en resolves to its exact entry (0.2); fr wins.
        selected.ShouldBe("fr");
    }

    // ============================================================================
    // Content-coding negotiation (RFC 9110 §12.5.3)
    // ============================================================================

    [Fact]
    public void SelectEncoding_ShouldPreferServerOrderOnTie()
    {
        HttpContentNegotiation.TrySelectEncoding("gzip, br", new[] { "br", "gzip" }, out string selected)
            .ShouldBeTrue();

        selected.ShouldBe("br");
    }

    [Fact]
    public void SelectEncoding_NoHeader_ShouldReturnIdentity()
    {
        HttpContentNegotiation.TrySelectEncoding((string?)null, new[] { "gzip" }, out string selected).ShouldBeTrue();

        selected.ShouldBe("identity");
    }

    [Fact]
    public void SelectEncoding_AcceptedCoding_ShouldCompressEvenAtLowQuality()
    {
        HttpContentNegotiation.TrySelectEncoding("gzip;q=0.5", new[] { "gzip" }, out string selected).ShouldBeTrue();

        selected.ShouldBe("gzip");
    }

    [Fact]
    public void SelectEncoding_IdentityExplicitlyPreferred_ShouldNotCompress()
    {
        HttpContentNegotiation.TrySelectEncoding("gzip;q=0.5, identity;q=0.9", new[] { "gzip" }, out string selected)
            .ShouldBeTrue();

        selected.ShouldBe("identity");
    }

    [Fact]
    public void SelectEncoding_UnsupportedCoding_ShouldFallBackToIdentity()
    {
        HttpContentNegotiation.TrySelectEncoding("br", new[] { "gzip" }, out string selected).ShouldBeTrue();

        selected.ShouldBe("identity");
    }

    [Fact]
    public void SelectEncoding_WildcardRefusesIdentity_AndNoCodingAvailable_ShouldReturnFalse()
    {
        HttpContentNegotiation.TrySelectEncoding("*;q=0", new[] { "gzip" }, out string selected).ShouldBeFalse();
        selected.ShouldBe(string.Empty);
    }

    [Fact]
    public void SelectEncoding_IdentityForbidden_ButCodingAvailable_ShouldCompress()
    {
        HttpContentNegotiation.TrySelectEncoding("gzip, identity;q=0", new[] { "gzip" }, out string selected)
            .ShouldBeTrue();

        selected.ShouldBe("gzip");
    }
}
