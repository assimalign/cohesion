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

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: An empty :protocol is an extended CONNECT violation on every method")]
    [InlineData("CONNECT")]
    [InlineData("GET")]
    public void ValidateExtendedConnect_OnEmptyProtocol_ShouldReturnViolation(string method)
    {
        // Arrange — RFC 9110 §5.6.2: a protocol name is a token (1*tchar), so "" names no protocol.

        // Act
        string? violation = HttpFieldNormalization.ValidateExtendedConnect(method, "https", "/chat", "api.test", string.Empty);

        // Assert
        violation.ShouldNotBeNull();
        violation.ShouldContain("':protocol'");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: An absent :protocol is not validated")]
    public void ValidateExtendedConnect_OnAbsentProtocol_ShouldReturnNull()
    {
        // Arrange — a classic CONNECT carries only :method and :authority (RFC 9113 §8.5).

        // Act
        string? violation = HttpFieldNormalization.ValidateExtendedConnect("CONNECT", null, null, "api.test:443", null);

        // Assert
        violation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A complete extended CONNECT is valid")]
    public void ValidateExtendedConnect_OnCompleteExtendedConnect_ShouldReturnNull()
    {
        // Act
        string? violation = HttpFieldNormalization.ValidateExtendedConnect("CONNECT", "https", "/chat", "api.test", "websocket");

        // Assert
        violation.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: A :protocol on a method other than CONNECT is a violation")]
    public void ValidateExtendedConnect_OnProtocolWithoutConnect_ShouldReturnViolation()
    {
        // Act
        string? violation = HttpFieldNormalization.ValidateExtendedConnect("GET", "https", "/", "api.test", "websocket");

        // Assert
        violation.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpFieldNormalization: Only a CONNECT with a non-empty :protocol is an extended CONNECT")]
    [InlineData("CONNECT", "websocket", true)]
    [InlineData("CONNECT", "", false)]
    [InlineData("CONNECT", null, false)]
    [InlineData("GET", "websocket", false)]
    [InlineData("GET", "", false)]
    [InlineData(null, "websocket", false)]
    public void IsExtendedConnect_ShouldRequireConnectAndNonEmptyProtocol(string? method, string? protocol, bool expected)
    {
        // Act
        bool isExtendedConnect = HttpFieldNormalization.IsExtendedConnect(method, protocol);

        // Assert
        isExtendedConnect.ShouldBe(expected);
    }
}
