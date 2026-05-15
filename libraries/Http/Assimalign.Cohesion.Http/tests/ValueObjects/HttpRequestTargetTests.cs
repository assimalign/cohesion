using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

/// <summary>
/// RFC 9112 &#167; 3.2 compliance tests for <see cref="HttpRequestTarget"/>. Covers the
/// four canonical forms (origin / absolute / authority / asterisk), method/form pairing
/// rules, and a malformed-input corpus.
/// </summary>
public class HttpRequestTargetTests
{
    // ============================================================================
    // origin-form (RFC 9112 §3.2.1)
    // ============================================================================

    [Theory]
    [InlineData("/", "/", "")]
    [InlineData("/index.html", "/index.html", "")]
    [InlineData("/v1/widgets", "/v1/widgets", "")]
    [InlineData("/v1/widgets?id=42", "/v1/widgets", "id=42")]
    [InlineData("/path?a=1&b=2&c=3", "/path", "a=1&b=2&c=3")]
    [InlineData("/with%20space", "/with%20space", "")]
    [InlineData("/?", "/", "")]
    public void OriginForm_ValidTargets_ShouldParse(string raw, string expectedPath, string expectedQuery)
    {
        bool ok = HttpRequestTarget.TryParse(raw, HttpMethod.Get, out HttpRequestTarget target);

        ok.ShouldBeTrue();
        target.Form.ShouldBe(HttpRequestTargetForm.Origin);
        target.Path.Value.ShouldBe(expectedPath);
        target.Query.Value.ShouldBe(expectedQuery);
        target.Host.ShouldBe(HttpHost.Empty);
        target.Scheme.ShouldBe(HttpScheme.None);
        target.RawValue.ShouldBe(raw);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void OriginForm_AllowedForEveryNonConnectMethod(string methodName)
    {
        HttpMethod method = HttpMethod.GetCanonicalizedValue(methodName);

        bool ok = HttpRequestTarget.TryParse("/path", method, out HttpRequestTarget target);

        ok.ShouldBeTrue();
        target.Form.ShouldBe(HttpRequestTargetForm.Origin);
    }

    // ============================================================================
    // absolute-form (RFC 9112 §3.2.2)
    // ============================================================================

    [Theory]
    [InlineData("http://example.com/", HttpScheme.Http, "example.com", "/", "")]
    [InlineData("https://example.com/", HttpScheme.Https, "example.com", "/", "")]
    [InlineData("http://example.com/path", HttpScheme.Http, "example.com", "/path", "")]
    [InlineData("http://example.com:8080/", HttpScheme.Http, "example.com:8080", "/", "")]
    [InlineData("https://example.com:8443/api?q=1", HttpScheme.Https, "example.com:8443", "/api", "q=1")]
    [InlineData("http://api.example.com/v1/things?x=1&y=2", HttpScheme.Http, "api.example.com", "/v1/things", "x=1&y=2")]
    public void AbsoluteForm_ValidTargets_ShouldParse(
        string raw, HttpScheme expectedScheme, string expectedHost, string expectedPath, string expectedQuery)
    {
        bool ok = HttpRequestTarget.TryParse(raw, HttpMethod.Get, out HttpRequestTarget target);

        ok.ShouldBeTrue();
        target.Form.ShouldBe(HttpRequestTargetForm.Absolute);
        target.Scheme.ShouldBe(expectedScheme);
        target.Host.Value.ShouldBe(expectedHost);
        target.Path.Value.ShouldBe(expectedPath);
        target.Query.Value.ShouldBe(expectedQuery);
        target.RawValue.ShouldBe(raw);
    }

    [Fact]
    public void AbsoluteForm_UnsupportedScheme_ShouldFail()
    {
        bool ok = HttpRequestTarget.TryParse("ftp://example.com/", HttpMethod.Get, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("ftp");
    }

    [Fact]
    public void AbsoluteForm_MissingAuthority_ShouldFail()
    {
        // "http:///path" — scheme + "//" + empty authority. URI parser accepts this but
        // we reject because RFC 9112 §3.2.2 requires a non-empty authority.
        bool ok = HttpRequestTarget.TryParse("http:///path", HttpMethod.Get, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    // ============================================================================
    // authority-form (RFC 9112 §3.2.3) — CONNECT only
    // ============================================================================

    [Theory]
    [InlineData("example.com:443")]
    [InlineData("api.example.com:8080")]
    [InlineData("10.0.0.1:443")]
    [InlineData("[::1]:8080")]
    [InlineData("[2001:db8::1]:443")]
    public void AuthorityForm_ValidConnectTargets_ShouldParse(string raw)
    {
        bool ok = HttpRequestTarget.TryParse(raw, HttpMethod.Connect, out HttpRequestTarget target);

        ok.ShouldBeTrue();
        target.Form.ShouldBe(HttpRequestTargetForm.Authority);
        target.Host.Value.ShouldBe(raw);
        target.Path.ShouldBe(HttpPath.Root);
        target.Query.Value.ShouldBe(string.Empty);
        target.Scheme.ShouldBe(HttpScheme.None);
    }

    [Fact]
    public void AuthorityForm_WithoutPort_ShouldFail()
    {
        bool ok = HttpRequestTarget.TryParse("example.com", HttpMethod.Connect, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("port");
    }

    [Fact]
    public void AuthorityForm_NonNumericPort_ShouldFail()
    {
        bool ok = HttpRequestTarget.TryParse("example.com:abc", HttpMethod.Connect, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("non-digit");
    }

    [Fact]
    public void AuthorityForm_UnmatchedIPv6Bracket_ShouldFail()
    {
        bool ok = HttpRequestTarget.TryParse("[::1:8080", HttpMethod.Connect, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("'['");
    }

    [Fact]
    public void AuthorityForm_IPv6WithoutPort_ShouldFail()
    {
        bool ok = HttpRequestTarget.TryParse("[::1]", HttpMethod.Connect, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("port");
    }

    [Fact]
    public void AuthorityForm_OnNonConnectMethod_ShouldFail()
    {
        // GET example.com:443 is malformed; CONNECT is the only method that takes
        // authority-form.
        bool ok = HttpRequestTarget.TryParse("example.com:443", HttpMethod.Get, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void ConnectMethod_WithOriginForm_ShouldFail()
    {
        // CONNECT /path HTTP/1.1 is malformed; CONNECT requires authority-form only.
        bool ok = HttpRequestTarget.TryParse("/path", HttpMethod.Connect, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("port");
    }

    // ============================================================================
    // asterisk-form (RFC 9112 §3.2.4) — OPTIONS only
    // ============================================================================

    [Fact]
    public void AsteriskForm_WithOptions_ShouldParse()
    {
        bool ok = HttpRequestTarget.TryParse("*", HttpMethod.Options, out HttpRequestTarget target);

        ok.ShouldBeTrue();
        target.Form.ShouldBe(HttpRequestTargetForm.Asterisk);
        target.RawValue.ShouldBe("*");
        target.Path.Value.ShouldBe("*");
        target.Host.ShouldBe(HttpHost.Empty);
        target.Scheme.ShouldBe(HttpScheme.None);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("HEAD")]
    [InlineData("CONNECT")]
    public void AsteriskForm_OnNonOptionsMethod_ShouldFail(string methodName)
    {
        HttpMethod method = HttpMethod.GetCanonicalizedValue(methodName);

        bool ok = HttpRequestTarget.TryParse("*", method, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void AsteriskForm_StaticConstantMatchesParsed()
    {
        HttpRequestTarget parsed = HttpRequestTarget.Parse("*", HttpMethod.Options);

        parsed.ShouldBe(HttpRequestTarget.Asterisk);
    }

    // ============================================================================
    // Malformed-target compliance corpus
    // ============================================================================

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/path with space")]
    [InlineData("/path\twith-tab")]
    [InlineData("/path\rwith-cr")]
    [InlineData("/path\nwith-lf")]
    [InlineData("/path with-null")]
    [InlineData("/pathwith-del")]
    public void Malformed_TargetsWithControlOrSpace_ShouldFail(string raw)
    {
        bool ok = HttpRequestTarget.TryParse(raw, HttpMethod.Get, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("nope")]                 // bare token — no leading '/' or scheme
    [InlineData("example.com/path")]     // looks like origin but missing leading '/'
    [InlineData(":://")]                 // empty scheme
    [InlineData("http://")]              // scheme + "//" with empty authority
    public void Malformed_NonStandardForms_ShouldFail(string raw)
    {
        bool ok = HttpRequestTarget.TryParse(raw, HttpMethod.Get, out _, out string? error);

        ok.ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void OriginForm_ProtocolRelative_IsValidPath()
    {
        // RFC 3986 §3.3 allows empty segments, so "//missing-scheme" is a legitimate
        // origin-form path even though it looks like a protocol-relative URI. The
        // server-side application can normalize if it wants stricter handling.
        bool ok = HttpRequestTarget.TryParse("//missing-scheme", HttpMethod.Get, out HttpRequestTarget target);

        ok.ShouldBeTrue();
        target.Form.ShouldBe(HttpRequestTargetForm.Origin);
        target.Path.Value.ShouldBe("//missing-scheme");
    }

    [Fact]
    public void Parse_OnMalformedInput_ShouldThrowHttpException()
    {
        HttpException ex = Should.Throw<HttpException>(() =>
            HttpRequestTarget.Parse("", HttpMethod.Get));

        ex.Code.ShouldBe(HttpErrorCode.InvalidRequestTarget);
    }

    // ============================================================================
    // Equality + ToString
    // ============================================================================

    [Fact]
    public void Equals_ReturnsTrueForIdenticalTargets()
    {
        HttpRequestTarget a = HttpRequestTarget.Parse("/api?x=1", HttpMethod.Get);
        HttpRequestTarget b = HttpRequestTarget.Parse("/api?x=1", HttpMethod.Get);

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        (a == b).ShouldBeTrue();
        (a != b).ShouldBeFalse();
    }

    [Fact]
    public void Equals_ReturnsFalseForDifferentForms()
    {
        HttpRequestTarget origin = HttpRequestTarget.Parse("/api", HttpMethod.Get);
        HttpRequestTarget absolute = HttpRequestTarget.Parse("http://example.com/api", HttpMethod.Get);

        origin.ShouldNotBe(absolute);
    }

    [Fact]
    public void ToString_ReturnsRawValue()
    {
        HttpRequestTarget target = HttpRequestTarget.Parse("/v1/widgets?id=42", HttpMethod.Get);

        target.ToString().ShouldBe("/v1/widgets?id=42");
    }
}
