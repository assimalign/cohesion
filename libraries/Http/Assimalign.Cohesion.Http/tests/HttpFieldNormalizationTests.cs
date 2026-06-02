using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpFieldNormalizationTests
{
    [Fact]
    public void ResolveAuthority_WhenAuthorityPresent_ShouldWinOverHost()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Host] = "host-header.test";

        HttpHost host = HttpFieldNormalization.ResolveAuthority("authority.test", headers);

        host.Value.ShouldBe("authority.test");
    }

    [Fact]
    public void ResolveAuthority_WhenAuthorityAbsent_ShouldFallBackToHost()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Host] = "host-header.test";

        HttpFieldNormalization.ResolveAuthority(null, headers).Value.ShouldBe("host-header.test");
        HttpFieldNormalization.ResolveAuthority("   ", headers).Value.ShouldBe("host-header.test");
    }

    [Fact]
    public void ResolveAuthority_WhenNeitherPresent_ShouldBeEmpty()
    {
        HttpHeaderCollection headers = new();

        HttpFieldNormalization.ResolveAuthority(null, headers).ShouldBe(HttpHost.Empty);
    }

    [Theory]
    [InlineData("Connection", true)]
    [InlineData("Keep-Alive", true)]
    [InlineData("Proxy-Connection", true)]
    [InlineData("Transfer-Encoding", true)]
    [InlineData("Upgrade", true)]
    [InlineData("TE", false)] // TE is handled separately (allowed with a restricted value)
    [InlineData("Content-Type", false)]
    public void IsForbiddenInHttp2Or3_ShouldClassify(string name, bool expected)
    {
        HttpFieldNormalization.IsForbiddenInHttp2Or3(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("trailers", true)]
    [InlineData("TRAILERS", true)]
    [InlineData("", true)] // empty == absent
    [InlineData("gzip", false)]
    [InlineData("trailers, deflate", false)]
    public void IsTeValueValidInHttp2Or3_ShouldClassify(string value, bool expected)
    {
        HttpFieldNormalization.IsTeValueValidInHttp2Or3(new HttpHeaderValue(value)).ShouldBe(expected);
    }

    [Fact]
    public void CombineFieldValue_OnCookie_ShouldCoalesceWithSemicolon()
    {
        HttpHeaderValue combined = HttpFieldNormalization.CombineFieldValue(
            HttpHeaderKey.Cookie, new HttpHeaderValue("a=1"), new HttpHeaderValue("b=2"));

        combined.Value.ShouldBe("a=1; b=2");
    }

    [Fact]
    public void CombineFieldValue_OnSetCookie_ShouldKeepDistinctValues()
    {
        HttpHeaderValue combined = HttpFieldNormalization.CombineFieldValue(
            HttpHeaderKey.SetCookie, new HttpHeaderValue("a=1"), new HttpHeaderValue("b=2"));

        // Set-Cookie must never be folded into one comma line: the two cookies
        // remain distinct values.
        combined.Count.ShouldBe(2);
        combined[0].ShouldBe("a=1");
        combined[1].ShouldBe("b=2");
    }

    [Fact]
    public void CombineFieldValue_OnListField_ShouldCombineAsValues()
    {
        HttpHeaderValue combined = HttpFieldNormalization.CombineFieldValue(
            HttpHeaderKey.Accept, new HttpHeaderValue("text/html"), new HttpHeaderValue("application/json"));

        combined.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("CONNECT", "websocket", true)]
    [InlineData("GET", "websocket", false)]
    [InlineData("CONNECT", null, false)]
    [InlineData("CONNECT", "", false)]
    [InlineData(null, "websocket", false)]
    public void IsExtendedConnect_ClassifiesCorrectly(string? method, string? protocol, bool expected)
    {
        HttpFieldNormalization.IsExtendedConnect(method, protocol).ShouldBe(expected);
    }

    [Fact]
    public void ValidateExtendedConnect_WhenNoProtocol_ShouldReturnNull()
    {
        // No :protocol — an ordinary request (or classic CONNECT); nothing to validate.
        HttpFieldNormalization.ValidateExtendedConnect("GET", "https", "/", "a", protocol: null).ShouldBeNull();
        HttpFieldNormalization.ValidateExtendedConnect("CONNECT", null, null, "a", protocol: null).ShouldBeNull();
    }

    [Fact]
    public void ValidateExtendedConnect_WhenValid_ShouldReturnNull()
    {
        HttpFieldNormalization.ValidateExtendedConnect("CONNECT", "https", "/chat", "api.test", "websocket").ShouldBeNull();
    }

    [Fact]
    public void ValidateExtendedConnect_WhenProtocolOnNonConnect_ShouldReturnError()
    {
        // RFC 8441 §4 — :protocol is only valid on CONNECT.
        HttpFieldNormalization.ValidateExtendedConnect("GET", "https", "/", "api.test", "websocket").ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null, "/chat", "api.test")]   // missing :scheme
    [InlineData("https", null, "api.test")]   // missing :path
    [InlineData("https", "/chat", null)]      // missing :authority
    public void ValidateExtendedConnect_WhenMissingRequiredPseudoHeader_ShouldReturnError(string? scheme, string? path, string? authority)
    {
        // RFC 8441 §4 — an extended CONNECT MUST include :scheme, :path, :authority.
        HttpFieldNormalization.ValidateExtendedConnect("CONNECT", scheme, path, authority, "websocket").ShouldNotBeNull();
    }
}
