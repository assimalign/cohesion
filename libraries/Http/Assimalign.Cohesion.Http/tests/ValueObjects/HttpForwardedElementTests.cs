using System.Net;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 7239 &#167; 4 compliance tests for <see cref="HttpForwardedElement"/>: forwarded-pair parsing,
/// typed <c>for</c>/<c>by</c>/<c>host</c>/<c>proto</c> access, quoted-string handling, extension-pair
/// preservation, serialization round-trips, and deterministic rejection.
/// </summary>
public class HttpForwardedElementTests
{
    // ============================================================================
    // Registered parameters
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should parse a single for pair")]
    public void TryParse_ForOnly_ShouldExposeForNode()
    {
        bool ok = HttpForwardedElement.TryParse("for=192.0.2.43", out HttpForwardedElement element);

        ok.ShouldBeTrue();
        element.For.ShouldNotBeNull();
        element.For!.Value.Address.ShouldBe(IPAddress.Parse("192.0.2.43"));
        element.By.ShouldBeNull();
        element.Host.ShouldBeNull();
        element.Proto.ShouldBeNull();
        element.Parameters.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should parse for, by, host and proto together")]
    public void TryParse_AllRegistered_ShouldExposeEach()
    {
        bool ok = HttpForwardedElement.TryParse("for=192.0.2.43;by=203.0.113.43;host=example.com;proto=https", out HttpForwardedElement element);

        ok.ShouldBeTrue();
        element.For!.Value.Address.ShouldBe(IPAddress.Parse("192.0.2.43"));
        element.By!.Value.Address.ShouldBe(IPAddress.Parse("203.0.113.43"));
        element.Host.ShouldBe("example.com");
        element.Proto.ShouldBe("https");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should lower-case parameter names but preserve value case")]
    public void TryParse_MixedCaseNames_ShouldLowerNamesKeepValues()
    {
        bool ok = HttpForwardedElement.TryParse("For=192.0.2.43;pRoTo=HTTPS", out HttpForwardedElement element);

        ok.ShouldBeTrue();
        element.Parameters[0].Name.ShouldBe("for");
        element.Parameters[1].Name.ShouldBe("proto");
        element.Proto.ShouldBe("HTTPS");
    }

    // ============================================================================
    // Quoted values — ports, IPv6, host:port
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should parse a quoted IPv6 node with a port")]
    public void TryParse_QuotedIPv6ForWithPort_ShouldUnquoteAndParse()
    {
        bool ok = HttpForwardedElement.TryParse("for=\"[2001:db8:cafe::17]:4711\"", out HttpForwardedElement element);

        ok.ShouldBeTrue();
        element.For!.Value.Address.ShouldBe(IPAddress.Parse("2001:db8:cafe::17"));
        element.For!.Value.PortNumber.ShouldBe(4711);
        element.Parameters[0].Value.ShouldBe("[2001:db8:cafe::17]:4711");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should parse a quoted host with a port")]
    public void TryParse_QuotedHostWithPort_ShouldUnquote()
    {
        bool ok = HttpForwardedElement.TryParse("host=\"example.com:8080\"", out HttpForwardedElement element);

        ok.ShouldBeTrue();
        element.Host.ShouldBe("example.com:8080");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should resolve backslash escapes inside a quoted value")]
    public void TryParse_QuotedValueWithEscape_ShouldUnescape()
    {
        bool ok = HttpForwardedElement.TryParse("host=\"a\\\"b\"", out HttpForwardedElement element);

        ok.ShouldBeTrue();
        element.Host.ShouldBe("a\"b");
    }

    // ============================================================================
    // Extension parameters + empty-pair tolerance
    // ============================================================================

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should preserve extension parameters")]
    public void TryParse_ExtensionParameter_ShouldBePreserved()
    {
        bool ok = HttpForwardedElement.TryParse("for=192.0.2.43;secret=value", out HttpForwardedElement element);

        ok.ShouldBeTrue();
        element.TryGetParameter("secret", out string? value).ShouldBeTrue();
        value.ShouldBe("value");
        element.Parameters.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should ignore empty forwarded-pairs")]
    public void TryParse_EmptyPairs_ShouldBeSkipped()
    {
        bool ok = HttpForwardedElement.TryParse(";for=192.0.2.43;;proto=http;", out HttpForwardedElement element);

        ok.ShouldBeTrue();
        element.Parameters.Count.ShouldBe(2);
        element.Proto.ShouldBe("http");
    }

    // ============================================================================
    // Serialization + Create
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should round-trip through Serialize")]
    [InlineData("for=192.0.2.43")]
    [InlineData("for=192.0.2.43;proto=https")]
    [InlineData("for=\"[2001:db8:cafe::17]:4711\"")]
    [InlineData("host=\"example.com:8080\";proto=https")]
    public void TryParse_ThenSerialize_ShouldRoundTrip(string raw)
    {
        HttpForwardedElement.TryParse(raw, out HttpForwardedElement element).ShouldBeTrue();

        element.Serialize().ShouldBe(raw);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should serialize a quoted value with escaped quotes")]
    public void Serialize_ValueWithQuote_ShouldEscape()
    {
        HttpForwardedElement.TryParse("host=\"a\\\"b\"", out HttpForwardedElement element).ShouldBeTrue();

        element.Serialize().ShouldBe("host=\"a\\\"b\"");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should build an element from typed values")]
    public void Create_TypedValues_ShouldSerializeCanonically()
    {
        HttpForwardedElement element = HttpForwardedElement.Create(
            @for: HttpForwardedNode.FromIPAddress(IPAddress.Parse("192.0.2.43")),
            proto: "https");

        element.Serialize().ShouldBe("for=192.0.2.43;proto=https");
        element.For!.Value.Address.ShouldBe(IPAddress.Parse("192.0.2.43"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should quote a for node with a port built via Create")]
    public void Create_ForWithPort_ShouldQuoteOnSerialize()
    {
        HttpForwardedElement element = HttpForwardedElement.Create(
            @for: HttpForwardedNode.FromIPAddress(IPAddress.Parse("2001:db8::1"), 4711));

        element.Serialize().ShouldBe("for=\"[2001:db8::1]:4711\"");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should reject an extension that reuses a registered name")]
    public void Create_ExtensionReusingRegisteredName_ShouldThrow()
    {
        Should.Throw<System.ArgumentException>(() => HttpForwardedElement.Create(
            proto: "https",
            extensions: new[] { new HttpForwardedParameter("proto", "http") }));
    }

    // ============================================================================
    // Malformed — deterministic rejection, never throws
    // ============================================================================

    [Theory(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should reject malformed elements without throwing")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("for")]                       // no '='
    [InlineData("=192.0.2.43")]               // empty name
    [InlineData("for=")]                       // empty value
    [InlineData("for=notanode")]
    [InlineData("for=1.2.3.4;by=notanode")]
    [InlineData("for=192.0.2.43:47011")]      // ':' requires quoting; bare is malformed
    [InlineData("for=[2001:db8::1]")]         // IPv6 must be quoted
    [InlineData("bad name=value")]            // name is not a token
    [InlineData("proto=\"unterminated")]      // unterminated quoted-string
    [InlineData("proto=\"a\\\"")]              // quote closed only by an escaped quote
    [InlineData(";;;")]                        // only empty pairs
    public void TryParse_Malformed_ShouldFail(string raw)
    {
        bool ok = true;
        Should.NotThrow(() => ok = HttpForwardedElement.TryParse(raw, out _));

        ok.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - ForwardedElement: Should throw HttpException from Parse on malformed input")]
    public void Parse_Malformed_ShouldThrowHttpException()
    {
        Should.Throw<HttpException>(() => HttpForwardedElement.Parse("for=notanode"));
    }
}
