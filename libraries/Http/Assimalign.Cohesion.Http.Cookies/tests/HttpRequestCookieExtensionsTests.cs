using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

public class HttpRequestCookieExtensionsTests
{
    [Fact]
    public void Cookies_NoCookieHeader_ShouldReturnEmptyCollection()
    {
        IHttpContext context = new BareHttpContext();

        IHttpCookieCollection cookies = context.Request.Cookies;

        cookies.Count.ShouldBe(0);
    }

    [Fact]
    public void Cookies_NoCookieHeader_ShouldStillInstallFeatureOnRead()
    {
        // First read primes the feature with an empty parse so subsequent
        // reads don't re-tokenize the (still empty) header. The presence of
        // the feature signals "Cookies has been observed for this exchange",
        // which middleware can use as a signal even when no cookies were sent.
        IHttpContext context = new BareHttpContext();

        _ = context.Request.Cookies;

        context.Features.Get<IHttpRequestCookieFeature>().ShouldNotBeNull();
    }

    [Fact]
    public void Cookies_SingleCookieHeader_ShouldParseAllNameValuePairs()
    {
        BareHttpContext context = new();
        context.RequestHeaders[HttpHeaderKey.Cookie] = "session=abc; theme=light";

        IHttpCookieCollection cookies = context.Request.Cookies;

        cookies.Count.ShouldBe(2);
        HttpCookie[] arr = ToArray(cookies);
        arr[0].Name.ShouldBe("session");
        arr[0].Value.ShouldBe("abc");
        arr[1].Name.ShouldBe("theme");
        arr[1].Value.ShouldBe("light");
    }

    [Fact]
    public void Cookies_MultipleCookieHeaderValues_ShouldParseAcrossAllValues()
    {
        // RFC 6265 §4.2.2 — a user agent SHOULD fold all cookies into a
        // single Cookie header, but RFC 9113 §8.2.3 lets HTTP/2 senders emit
        // them as separate fields. The extension should handle both shapes.
        BareHttpContext context = new();
        context.RequestHeaders[HttpHeaderKey.Cookie] = new HttpHeaderValue(new[] { "a=1", "b=2" });

        IHttpCookieCollection cookies = context.Request.Cookies;

        cookies.Count.ShouldBe(2);
    }

    [Fact]
    public void Cookies_MalformedSegments_ShouldBeSkipped()
    {
        // Empty segments and missing-name segments are dropped; missing-value
        // segments yield an empty value per the lenient cookie parsing convention.
        BareHttpContext context = new();
        context.RequestHeaders[HttpHeaderKey.Cookie] = "name=value; ; =orphan; flag";

        IHttpCookieCollection cookies = context.Request.Cookies;

        cookies.Count.ShouldBe(2);
        HttpCookie[] arr = ToArray(cookies);
        arr[0].Name.ShouldBe("name");
        arr[0].Value.ShouldBe("value");
        arr[1].Name.ShouldBe("flag");
        arr[1].Value.ShouldBe(string.Empty);
    }

    [Fact]
    public void Cookies_RepeatedReads_ShouldReturnSameInstance()
    {
        BareHttpContext context = new();
        context.RequestHeaders[HttpHeaderKey.Cookie] = "a=1";

        IHttpCookieCollection first = context.Request.Cookies;
        IHttpCookieCollection second = context.Request.Cookies;

        second.ShouldBeSameAs(first);
    }

    [Fact]
    public void Cookies_HeaderChangedAfterFirstRead_ShouldReturnCachedParse()
    {
        // Caching is by design: parsing happens once and subsequent reads
        // return the cached collection. Mutating the underlying header after
        // the first read does NOT re-trigger parsing.
        BareHttpContext context = new();
        context.RequestHeaders[HttpHeaderKey.Cookie] = "a=1";

        IHttpCookieCollection first = context.Request.Cookies;
        context.RequestHeaders[HttpHeaderKey.Cookie] = "a=1; b=2";
        IHttpCookieCollection second = context.Request.Cookies;

        second.ShouldBeSameAs(first);
        second.Count.ShouldBe(1); // pre-change parse, not the post-change header
    }

    [Fact]
    public void Cookies_PreInstalledFeature_ShouldBeObservedByGetter()
    {
        BareHttpContext context = new();
        HttpCookieCollection seeded = new() { new HttpCookie("seeded", "value") };
        context.Features.Set<IHttpRequestCookieFeature>(new TestRequestCookieFeature(seeded));

        IHttpCookieCollection observed = context.Request.Cookies;

        observed.ShouldBeSameAs(seeded);
    }

    [Fact]
    public void Cookies_GetOnNullRequest_ShouldThrow()
    {
        IHttpRequest request = null!;

        Should.Throw<ArgumentNullException>(() => _ = request.Cookies);
    }

    private static HttpCookie[] ToArray(IHttpCookieCollection cookies)
    {
        HttpCookie[] arr = new HttpCookie[cookies.Count];
        cookies.CopyTo(arr, 0);
        return arr;
    }

    private sealed class TestRequestCookieFeature : IHttpRequestCookieFeature
    {
        public TestRequestCookieFeature(IHttpCookieCollection cookies)
        {
            Cookies = cookies;
        }

        public IHttpCookieCollection Cookies { get; }
    }

    /// <summary>
    /// Bare-bones <see cref="IHttpContext"/> stub with a real request that
    /// holds a back-reference to the context, so the request cookies
    /// extension property can reach the feature collection.
    /// </summary>
    private sealed class BareHttpContext : IHttpContext
    {
        public BareHttpContext()
        {
            Request = new BareHttpRequest(this);
            Response = new BareHttpResponse(this);
        }

        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request { get; }
        public IHttpResponse Response { get; }
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IHttpProtocolUpgrade? Upgrade => null;
        public IHttpFeatureCollection Features { get; } = new HttpFeatureCollection();
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestAborted => CancellationToken.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>Direct handle to the request's mutable header collection so tests can install a Cookie header.</summary>
        public IHttpHeaderCollection RequestHeaders => Request.Headers;
    }

    private sealed class BareHttpRequest : IHttpRequest
    {
        public BareHttpRequest(IHttpContext context)
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

    private sealed class BareHttpResponse : IHttpResponse
    {
        public BareHttpResponse(IHttpContext context)
        {
            HttpContext = context;
        }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext { get; }
        public Stream Body { get; set; } = Stream.Null;
    }
}
