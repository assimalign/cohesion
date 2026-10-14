using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

/// <summary>
/// Verifies extension attributes cannot inject cookie attributes or response fields,
/// and that received attributes remain safe to serialize after parsing.
/// </summary>
public class HttpCookieExtensionRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Extensions.Add: invalid input is refused before changing the collection")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x; Domain=evil.example")]
    [InlineData("x\ry")]
    [InlineData("x\ny")]
    [InlineData("x\0y")]
    [InlineData("x\u007fy")]
    [InlineData("x\ty")]
    [InlineData("x\u001fy")]
    [InlineData("x\u0080y")]
    [InlineData("x\u00a0y")]
    [InlineData("x\u0100y")]
    public void Add_InvalidExtension_ShouldThrowAndLeaveCollectionUnchanged(string? extension)
    {
        // Arrange
        HttpCookieOptions options = new();
        options.Extensions.Add("Priority=High");

        // Act
        Exception? refusal = Record.Exception(() => options.Extensions.Add(extension!));

        // Assert
        refusal.ShouldBeOfType<ArgumentException>();
        options.Extensions.Count.ShouldBe(1);
        new HttpCookie("sid", "abc", options).ToString().ShouldBe("sid=abc; Path=/; Priority=High");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Response cookies: a caught extension injection cannot alter the Set-Cookie line")]
    [InlineData("x; Domain=evil.example")]
    [InlineData("x; Path=/admin")]
    [InlineData("x; SameSite=None")]
    [InlineData("x\r\nSet-Cookie: role=admin")]
    [InlineData("x\r\nX-Injected: yes")]
    public void Cookies_ExtensionInjectionRefused_ShouldEmitOnlyExplicitAttributes(string extension)
    {
        // Arrange
        IHttpContext context = new TestHttpContext();
        HttpCookie cookie = new("sid", "abc", new HttpCookieOptions
        {
            Domain = "example.com",
            Path = "/app",
            Secure = true,
            HttpOnly = true,
            SameSite = HttpCookieSameSiteMode.Strict,
        });
        cookie.Options.Extensions.Add("Priority=High");

        // Act
        try
        {
            cookie.Options.Extensions.Add(extension);
        }
        catch (ArgumentException)
        {
            // Application error handling may continue to emit the original cookie.
        }
        context.Response.Cookies.Add(cookie);

        // Assert
        HttpHeaderValue setCookie = context.Response.Headers[HttpHeaderKey.SetCookie];
        setCookie.Count.ShouldBe(1);
        setCookie.Value.ShouldBe("sid=abc; Domain=example.com; Path=/app; Secure; HttpOnly; SameSite=Strict; Priority=High");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Response parse: unrepresentable extensions are dropped without losing the cookie")]
    [InlineData("Bad\tFlag")]
    [InlineData("Bad\0Flag")]
    [InlineData("Bad\u007fFlag")]
    [InlineData("Bad\u0080Flag")]
    [InlineData("Future=va\rlue")]
    [InlineData("Future=va\nlue")]
    [InlineData("Future=va\0lue")]
    [InlineData("Future=va\u007flue")]
    [InlineData("Future=va\u0080lue")]
    [InlineData("\rFuture=High")]
    [InlineData("\nFuture=High")]
    [InlineData("Future=High\r")]
    [InlineData("Future=High\n")]
    [InlineData("\u00a0Future=High")]
    [InlineData("Future=High\u00a0")]
    [InlineData("\rFlag")]
    [InlineData("\nFlag")]
    [InlineData("Flag\r")]
    [InlineData("Flag\n")]
    [InlineData("\u00a0Flag")]
    [InlineData("Flag\u00a0")]
    [InlineData("Expires=bad\tdate")]
    [InlineData("Max-Age=bad\tage")]
    [InlineData("SameSite=bad\tmode")]
    public void ResponseParse_UnrepresentableExtension_ShouldDropOnlyThatAttribute(string extension)
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = "id=42; Priority=High; " + extension + "; Partitioned; Secure";

        // Act
        HttpCookieCollection cookies = Should.NotThrow(() => new HttpCookieCollection(headers, HttpHeaderKey.SetCookie));

        // Assert
        HttpCookie cookie = cookies.ShouldHaveSingleItem();
        cookie.Options.Extensions.Count.ShouldBe(2);
        cookie.ToString().ShouldBe("id=42; Secure; Priority=High; Partitioned");
    }

    // Compatibility coverage: these valid-attribute round trips already worked before #1474.
    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - Response parse compatibility: valid extension attributes still round trip")]
    [InlineData("Priority=High", "Priority=High")]
    [InlineData("Partitioned", "Partitioned")]
    [InlineData("Future=a=b", "Future=a=b")]
    [InlineData("Future=\"quoted value\",\\~", "Future=\"quoted value\",\\~")]
    [InlineData("Future=", "Future=")]
    [InlineData("Expires=soon", "Expires=soon")]
    [InlineData("Max-Age=forever", "Max-Age=forever")]
    [InlineData("SameSite=future", "SameSite=future")]
    [InlineData(" \tPriority \t= \tHigh \t", "Priority=High")]
    [InlineData(" \tPartitioned \t", "Partitioned")]
    public void ResponseParse_ValidExtension_ShouldPreserveRoundTrip(string extension, string expected)
    {
        // Arrange
        HttpHeaderCollection headers = new();
        headers[HttpHeaderKey.SetCookie] = "id=42; " + extension;

        // Act
        HttpCookieCollection cookies = new(headers, HttpHeaderKey.SetCookie);
        HttpCookie cookie = cookies.ShouldHaveSingleItem();
        string serialized = cookie.ToString();
        HttpHeaderCollection roundTripHeaders = new();
        roundTripHeaders[HttpHeaderKey.SetCookie] = serialized;
        HttpCookieCollection roundTrip = new(roundTripHeaders, HttpHeaderKey.SetCookie);

        // Assert
        cookie.Options.Extensions.Count.ShouldBe(1);
        serialized.ShouldBe("id=42; " + expected);
        roundTrip.ShouldHaveSingleItem().ToString().ShouldBe(serialized);
    }

    private sealed class TestHttpContext : IHttpContext
    {
        public TestHttpContext()
        {
            Request = new TestHttpRequest(this);
            Response = new TestHttpResponse(this);
        }

        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request { get; }
        public IHttpResponse Response { get; }
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestCancelled => CancellationToken.None;

        public void Cancel()
        {
        }

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestHttpRequest : IHttpRequest
    {
        public TestHttpRequest(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => HttpPath.Root;
        public HttpMethod Method => HttpMethod.Get;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body => Stream.Null;
    }

    private sealed class TestHttpResponse : IHttpResponse
    {
        public TestHttpResponse(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; } = Stream.Null;
    }
}
