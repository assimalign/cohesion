using System;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

/// <summary>
/// Focused tests for the sync-aware
/// <see cref="HttpCookieCollection"/> behaviour: every mutation writes
/// through to the bound header, and the header round-trips back to the
/// same set of cookies on re-parse.
/// </summary>
public class HttpCookieCollectionTests
{
    [Fact]
    public void Parameterless_Ctor_ShouldStartEmpty()
    {
        HttpCookieCollection cookies = new();

        cookies.Count.ShouldBe(0);
        cookies.IsReadOnly.ShouldBeFalse();
    }

    [Fact]
    public void Parameterless_Ctor_ShouldHoldCollectionInitializerItems()
    {
        HttpCookieCollection cookies = new()
        {
            new HttpCookie("a", "1"),
            new HttpCookie("b", "2"),
        };

        cookies.Count.ShouldBe(2);
    }

    [Fact]
    public void RequestSide_OnCtor_ShouldParseCookieHeaderIntoCollection()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Cookie] = "session=abc; theme=light";

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.Cookie);

        cookies.Count.ShouldBe(2);
        HttpCookie[] arr = cookies.ToArray();
        arr[0].Name.ShouldBe("session");
        arr[0].Value.ShouldBe("abc");
        arr[1].Name.ShouldBe("theme");
        arr[1].Value.ShouldBe("light");
    }

    [Fact]
    public void RequestSide_OnAdd_ShouldWriteThroughToCookieHeader()
    {
        HttpHeaderCollection headers = new();
        HttpCookieCollection cookies = new(headers, HttpHeaderKey.Cookie);

        cookies.Add(new HttpCookie("session", "abc"));
        cookies.Add(new HttpCookie("theme", "light"));

        headers[HttpHeaderKey.Cookie].Value.ShouldBe("session=abc; theme=light");
    }

    [Fact]
    public void RequestSide_OnClear_ShouldRemoveCookieHeader()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Cookie] = "session=abc";
        HttpCookieCollection cookies = new(headers, HttpHeaderKey.Cookie);

        cookies.Clear();

        headers.ContainsKey(HttpHeaderKey.Cookie).ShouldBeFalse();
        cookies.Count.ShouldBe(0);
    }

    [Fact]
    public void RequestSide_OnRemove_ShouldRewriteCookieHeader()
    {
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Cookie] = "a=1; b=2; c=3";
        HttpCookieCollection cookies = new(headers, HttpHeaderKey.Cookie);

        HttpCookie b = cookies.First(c => c.Name == "b");
        cookies.Remove(b).ShouldBeTrue();

        cookies.Count.ShouldBe(2);
        headers[HttpHeaderKey.Cookie].Value.ShouldBe("a=1; c=3");
    }

    [Fact]
    public void ResponseSide_OnAdd_ShouldAppendOneSetCookieValuePerCookie()
    {
        HttpHeaderCollection headers = new();
        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);

        cookies.Add(new HttpCookie("a", "1"));
        cookies.Add(new HttpCookie("b", "2"));

        // RFC 6265 §3 — multiple Set-Cookie values, one per cookie.
        HttpHeaderValue setCookie = headers[HttpHeaderKey.SetCookie];
        setCookie.Count.ShouldBe(2);
    }

    [Fact]
    public void ResponseSide_FullAttributeCookie_ShouldRoundTripThroughHeader()
    {
        // Verifies the response-side serializer round-trips every
        // attribute the HttpCookieOptions surface supports.
        HttpHeaderCollection headers = new();
        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);

        DateTimeOffset expires = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        cookies.Add(new HttpCookie("session", "abc123", new HttpCookieOptions
        {
            Domain = "example.com",
            Path = "/app",
            Expires = expires,
            MaxAge = TimeSpan.FromHours(1),
            Secure = true,
            HttpOnly = true,
            SameSite = HttpCookieSameSiteMode.Strict,
        }));

        // Re-parse from the header through a fresh collection on the
        // same header object. Cookies should come back equal.
        HttpCookieCollection reparsed = new(headers, HttpHeaderKey.SetCookie);

        reparsed.Count.ShouldBe(1);
        HttpCookie roundTripped = reparsed.Single();
        roundTripped.Name.ShouldBe("session");
        roundTripped.Value.ShouldBe("abc123");
        roundTripped.Options.Domain.ShouldBe("example.com");
        roundTripped.Options.Path.ShouldBe("/app");
        roundTripped.Options.Expires.ShouldBe(expires);
        roundTripped.Options.MaxAge.ShouldBe(TimeSpan.FromHours(1));
        roundTripped.Options.Secure.ShouldBeTrue();
        roundTripped.Options.HttpOnly.ShouldBeTrue();
        roundTripped.Options.SameSite.ShouldBe(HttpCookieSameSiteMode.Strict);
    }

    [Fact]
    public void ResponseSide_OnRemove_ShouldRewriteSetCookieHeaderValues()
    {
        HttpHeaderCollection headers = new();
        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);
        cookies.Add(new HttpCookie("a", "1"));
        cookies.Add(new HttpCookie("b", "2"));
        cookies.Add(new HttpCookie("c", "3"));

        HttpCookie b = cookies.First(c => c.Name == "b");
        cookies.Remove(b).ShouldBeTrue();

        HttpHeaderValue setCookie = headers[HttpHeaderKey.SetCookie];
        setCookie.Count.ShouldBe(2);
    }

    [Fact]
    public void ResponseSide_OnClear_ShouldRemoveSetCookieHeader()
    {
        HttpHeaderCollection headers = new();
        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);
        cookies.Add(new HttpCookie("a", "1"));

        cookies.Clear();

        headers.ContainsKey(HttpHeaderKey.SetCookie).ShouldBeFalse();
        cookies.Count.ShouldBe(0);
    }

    [Fact]
    public void ResponseSide_UnknownAttribute_ShouldRoundTripViaExtensions()
    {
        // Custom attributes (or future-shape Cohesion doesn't know yet)
        // round-trip through HttpCookieOptions.Extensions so a proxy /
        // middleware that re-emits the header doesn't silently lose them.
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = "id=42; Priority=High";

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);

        HttpCookie cookie = cookies.Single();
        cookie.Options.Extensions.ShouldContain("Priority=High");
    }

    [Fact]
    public void Ctor_NullHeaders_ShouldThrow()
    {
        Should.Throw<ArgumentNullException>(() => new HttpCookieCollection(null!, HttpHeaderKey.Cookie));
    }

    [Fact]
    public void RequestSide_MultipleCookieHeaderValues_ShouldParseAcrossAllValues()
    {
        // RFC 9113 §8.2.3 — HTTP/2 senders may emit separate Cookie field
        // lines. The collection coalesces them transparently.
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.Cookie] = new HttpHeaderValue(new[] { "a=1", "b=2" });

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.Cookie);

        cookies.Count.ShouldBe(2);
    }

    [Fact]
    public void ResponseSide_ParsesExpiresWithCommaInsideValue()
    {
        // Expires uses RFC 1123 format ("Wed, 09 Jun 2021 10:18:14 GMT")
        // which contains a comma. The Set-Cookie parser must split on ';'
        // only, never ',' — otherwise the date gets mangled.
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = "id=42; Expires=Wed, 09 Jun 2021 10:18:14 GMT";

        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);

        HttpCookie cookie = cookies.Single();
        cookie.Name.ShouldBe("id");
        cookie.Value.ShouldBe("42");
        cookie.Options.Expires.ShouldBe(new DateTimeOffset(2021, 6, 9, 10, 18, 14, TimeSpan.Zero));
    }
}
