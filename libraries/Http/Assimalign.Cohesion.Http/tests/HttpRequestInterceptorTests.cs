using System;
using System.IO;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpRequestInterceptorTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Should round-trip the max request body size knob")]
    public void Context_MaxRequestBodySize_ShouldRoundTrip()
    {
        HttpRequestInterceptorContext context = CreateContext(maxRequestBodySize: 1024);

        context.MaxRequestBodySize.ShouldBe(1024);

        context.MaxRequestBodySize = 2048;
        context.MaxRequestBodySize.ShouldBe(2048);

        context.MaxRequestBodySize = null;
        context.MaxRequestBodySize.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Should reject a negative max request body size")]
    public void Context_MaxRequestBodySize_OnNegative_ShouldThrow()
    {
        HttpRequestInterceptorContext context = CreateContext(maxRequestBodySize: 1024);

        Should.Throw<ArgumentOutOfRangeException>(() => context.MaxRequestBodySize = -1);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Should freeze the knob idempotently and reject later writes")]
    public void Context_Freeze_ShouldRejectLaterWrites()
    {
        HttpRequestInterceptorContext context = CreateContext(maxRequestBodySize: 1024);

        context.IsMaxRequestBodySizeReadOnly.ShouldBeFalse();

        context.FreezeMaxRequestBodySize();
        context.FreezeMaxRequestBodySize(); // idempotent

        context.IsMaxRequestBodySizeReadOnly.ShouldBeTrue();
        context.MaxRequestBodySize.ShouldBe(1024); // reads still work
        Should.Throw<InvalidOperationException>(() => context.MaxRequestBodySize = 5);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Interceptor: The base class virtual defaults should no-op and pass the body stream through")]
    public void Interceptor_Defaults_ShouldPassThrough()
    {
        HttpRequestInterceptorContext context = CreateContext(maxRequestBodySize: null);
        IHttpExchangeInterceptor interceptor = new NoOverrideInterceptor();
        using MemoryStream body = new();

        interceptor.AfterRequestHead(context); // must not throw
        interceptor.BeforeRequestBody(context); // must not throw
        interceptor.AfterRequestBody(context, body).ShouldBeSameAs(body);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Headers: AsReadOnly view should observe the live store and reject mutation")]
    public void Headers_AsReadOnly_ShouldObserveAndRejectMutation()
    {
        HttpHeaderCollection headers = new();
        headers[new HttpHeaderKey("Host")] = "api.test";

        HttpHeaderCollection view = headers.AsReadOnly();

        view.IsReadOnly.ShouldBeTrue();
        view[new HttpHeaderKey("Host")].Value.ShouldBe("api.test");

        // Reads observe the live store.
        headers[new HttpHeaderKey("X-Late")] = "1";
        view.ContainsKey(new HttpHeaderKey("X-Late")).ShouldBeTrue();

        // Every mutation path on the view fails loudly.
        Should.Throw<InvalidOperationException>(() => view[new HttpHeaderKey("Host")] = "evil.test");
        Should.Throw<InvalidOperationException>(() => view.Add(new HttpHeaderKey("X-New"), "v"));
        Should.Throw<InvalidOperationException>(() => view.Remove(new HttpHeaderKey("Host")));
        Should.Throw<InvalidOperationException>(() => view.Clear());

        // The original stays mutable, and a read-only view of a view is the same instance.
        headers.IsReadOnly.ShouldBeFalse();
        view.AsReadOnly().ShouldBeSameAs(view);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Headers: Multi-valued header arrays escaping a read-only view should be defensive copies")]
    public void Headers_EscapedArrays_ShouldBeDefensiveCopies()
    {
        // Repeated field lines are folded into an array-backed value (as the h1 header parser
        // does). If ToArray()/the implicit conversion returned the live backing array, a caller
        // holding only the read-only view could rewrite header values in place, bypassing the
        // fail-loud guard — a framing-desync primitive.
        HttpHeaderKey key = new("X-Multi");
        HttpHeaderCollection headers = new();
        headers[key] = "first";
        headers[key] = HttpHeaderValue.Concat(headers[key], "second");

        HttpHeaderCollection view = headers.AsReadOnly();

        string?[] escaped = view[key].ToArray();
        escaped.Length.ShouldBe(2);
        escaped[1] = "tampered";
        headers[key].ToArray()[1].ShouldBe("second");

        string?[]? viaImplicit = view[key];
        viaImplicit.ShouldNotBeNull();
        viaImplicit![0] = "tampered";
        headers[key].ToArray()[0].ShouldBe("first");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - RejectedException: Should accept only error statuses")]
    [InlineData(400)]
    [InlineData(431)]
    [InlineData(503)]
    public void RejectedException_OnErrorStatus_ShouldCarryStatus(int status)
    {
        HttpRequestRejectedException exception = new(new HttpStatusCode(status));

        exception.StatusCode.Value.ShouldBe(status);
        exception.Message.ShouldNotBeNullOrEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - RejectedException: Should reject non-error statuses")]
    [InlineData(200)]
    [InlineData(101)]
    [InlineData(302)]
    public void RejectedException_OnNonErrorStatus_ShouldThrow(int status)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpRequestRejectedException(new HttpStatusCode(status)));
    }

    private static HttpRequestInterceptorContext CreateContext(long? maxRequestBodySize)
    {
        return new HttpRequestInterceptorContext
        {
            Version = HttpVersion.Http11,
            Method = HttpMethod.Post,
            Path = new HttpPath("/upload"),
            Scheme = HttpScheme.Http,
            Host = new HttpHost("api.test"),
            Headers = new HttpHeaderCollection().AsReadOnly(),
            Features = new HttpFeatureCollection(),
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = maxRequestBodySize,
        };
    }

    private sealed class NoOverrideInterceptor : HttpExchangeInterceptor
    {
    }
}
