using System;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

/// <summary>
/// RFC 6265bis per-cookie size limits enforced by
/// <see cref="HttpCookieCollection"/> parsing: a cookie whose name+value
/// exceeds <see cref="HttpCookieLimits.MaxNameValueLength"/> is ignored, an
/// attribute exceeding <see cref="HttpCookieLimits.MaxAttributeValueLength"/>
/// is dropped, and attributes past <see cref="HttpCookieLimits.MaxAttributeCount"/>
/// are not retained. Oversized input is ignored, never thrown.
/// </summary>
public class HttpCookieLimitsTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - HttpCookieLimits: defaults match RFC 6265bis")]
    public void Default_ShouldMatchRfc6265bisValues()
    {
        HttpCookieLimits limits = HttpCookieLimits.Default;

        limits.MaxNameValueLength.ShouldBe(4096);
        limits.MaxAttributeValueLength.ShouldBe(1024);
        limits.MaxAttributeCount.ShouldBe(50);
        limits.MaxLifetime.ShouldBe(TimeSpan.FromDays(400));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Request parse: name+value exactly at the 4096 limit is kept")]
    public void RequestParse_NameValueAtLimit_ShouldKeep()
    {
        // name "n" (1) + value (4095) == 4096 octets.
        string value = new('a', 4095);
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Cookie] = "n=" + value;

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.Cookie);

        cookies.Count.ShouldBe(1);
        cookies.Single().Value.Length.ShouldBe(4095);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Request parse: name+value over the 4096 limit is ignored")]
    public void RequestParse_NameValueOverLimit_ShouldDrop()
    {
        // name "n" (1) + value (4096) == 4097 octets — one over.
        string value = new('a', 4096);
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Cookie] = "n=" + value;

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.Cookie);

        cookies.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Response parse: oversized cookie is ignored, sibling kept")]
    public void ResponseParse_OneOversizedCookie_ShouldDropOnlyThatValue()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = new HttpHeaderValue(new[]
        {
            "ok=1",
            "big=" + new string('a', 5000),
        });

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);

        cookies.Count.ShouldBe(1);
        cookies.Single().Name.ShouldBe("ok");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Response parse: attribute value at the 1024 limit is kept")]
    public void ResponseParse_AttributeValueAtLimit_ShouldKeep()
    {
        string attrValue = new('a', 1024);
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = "id=1; X=" + attrValue;

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);

        HttpCookie cookie = cookies.Single();
        cookie.Options.Extensions.ShouldContain("X=" + attrValue);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Response parse: oversized attribute is dropped, cookie retained")]
    public void ResponseParse_AttributeValueOverLimit_ShouldDropAttributeKeepCookie()
    {
        string attrValue = new('a', 1025);
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = "id=1; X=" + attrValue;

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);

        HttpCookie cookie = cookies.Single();
        cookie.Name.ShouldBe("id");
        cookie.Value.ShouldBe("1");
        cookie.Options.Extensions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Response parse: attributes past the count bound are ignored")]
    public void ResponseParse_AttributeCountExceeded_ShouldIgnoreExcess()
    {
        HttpCookieLimits limits = new() { MaxAttributeCount = 3 };
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = "id=1; A=1; B=2; C=3; D=4; E=5";

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie, limits);

        HttpCookie cookie = cookies.Single();
        cookie.Options.Extensions.Count.ShouldBe(3);
        cookie.Options.Extensions.ShouldContain("A=1");
        cookie.Options.Extensions.ShouldContain("C=3");
        cookie.Options.Extensions.ShouldNotContain("D=4");
        cookie.Options.Extensions.ShouldNotContain("E=5");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Request parse: custom name+value limit is honored")]
    public void RequestParse_CustomNameValueLimit_ShouldBeHonored()
    {
        HttpCookieLimits limits = new() { MaxNameValueLength = 10 };
        HttpHeaderCollection headers = new();
        // "keep" (2+4=6, kept) vs "drop" (2 + 20 = 22, dropped).
        headers[HttpHeaderKey.Cookie] = "id=keep; xl=" + new string('a', 20);

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.Cookie, limits);

        cookies.Count.ShouldBe(1);
        cookies.Single().Value.ShouldBe("keep");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Parse: oversized input is ignored, never thrown")]
    public void Parse_OversizedInput_ShouldNotThrow()
    {
        // A valid small cookie trailed by thousands of attribute segments: the
        // count bound caps retention and nothing throws.
        string flood = string.Concat(Enumerable.Repeat("x; ", 5000));
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = new HttpHeaderValue(new[]
        {
            "huge=" + new string('a', 100_000),
            "id=1; " + flood,
        });

        HttpCookieCollection cookies = Should.NotThrow(
            () => new HttpCookieCollection(headers, HttpHeaderKey.SetCookie));

        // The oversized cookie is dropped; the small one survives with its
        // attribute list bounded by the default MaxAttributeCount.
        cookies.Count.ShouldBe(1);
        cookies.Single().Options.Extensions.Count.ShouldBeLessThanOrEqualTo(HttpCookieLimits.DefaultMaxAttributeCount);
    }
}
