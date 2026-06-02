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
}
