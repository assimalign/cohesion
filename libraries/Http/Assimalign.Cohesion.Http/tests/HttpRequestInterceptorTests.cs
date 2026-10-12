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
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);

        context.MaxRequestBodySize.ShouldBe(1024);

        context.MaxRequestBodySize = 2048;
        context.MaxRequestBodySize.ShouldBe(2048);

        context.MaxRequestBodySize = null;
        context.MaxRequestBodySize.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Should reject a negative max request body size")]
    public void Context_MaxRequestBodySize_OnNegative_ShouldThrow()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);

        Should.Throw<ArgumentOutOfRangeException>(() => context.MaxRequestBodySize = -1);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Should freeze the knob idempotently and reject later writes")]
    public void Context_Freeze_ShouldRejectLaterWrites()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);

        context.IsMaxRequestBodySizeReadOnly.ShouldBeFalse();

        context.FreezeMaxRequestBodySize();
        context.FreezeMaxRequestBodySize(); // idempotent

        context.IsMaxRequestBodySizeReadOnly.ShouldBeTrue();
        context.MaxRequestBodySize.ShouldBe(1024); // reads still work
        Should.Throw<InvalidOperationException>(() => context.MaxRequestBodySize = 5);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: The minimum request-body data rate is optional and defaults to null")]
    public void Context_MinRequestBodyDataRate_OnUnseededContext_ShouldDefaultToNull()
    {
        // Arrange / Act — a construction site that predates the member still compiles (#1383).
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);

        // Assert
        context.MinRequestBodyDataRate.ShouldBeNull();
        context.IsMinRequestBodyDataRateReadOnly.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Should lower, raise and disable the minimum request-body data rate before the freeze")]
    public void Context_MinRequestBodyDataRate_BeforeFreeze_ShouldRoundTrip()
    {
        // Arrange
        HttpMinDataRate seeded = new(240, TimeSpan.FromSeconds(5));
        HttpMinDataRate raised = new(4096, TimeSpan.FromSeconds(1));
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);
        context.MinRequestBodyDataRate = seeded;

        // Act and assert
        context.MinRequestBodyDataRate.ShouldBeSameAs(seeded);
        context.MinRequestBodyDataRate = raised;
        context.MinRequestBodyDataRate.ShouldBeSameAs(raised);
        context.MinRequestBodyDataRate = null;
        context.MinRequestBodyDataRate.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Should freeze the minimum request-body data rate idempotently and reject later writes")]
    public void Context_FreezeMinRequestBodyDataRate_ShouldRejectLaterWrites()
    {
        // Arrange
        HttpMinDataRate seeded = new(240, TimeSpan.FromSeconds(5));
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);
        context.MinRequestBodyDataRate = seeded;

        // Act
        context.FreezeMinRequestBodyDataRate();
        context.FreezeMinRequestBodyDataRate();

        // Assert — the rate freezes on its own: the body-size knob is still writable, and reads still work.
        context.IsMinRequestBodyDataRateReadOnly.ShouldBeTrue();
        context.MinRequestBodyDataRate.ShouldBeSameAs(seeded);
        Should.Throw<InvalidOperationException>(() => context.MinRequestBodyDataRate = null);
        context.IsMaxRequestBodySizeReadOnly.ShouldBeFalse();
        context.MaxRequestBodySize = 2048;
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: The minimum response data rate is optional, adjustable, and frozen on its own")]
    public void Context_MinResponseDataRate_ShouldRoundTripUntilFrozen()
    {
        // Arrange — a construction site that predates the member still compiles (#1384).
        HttpMinDataRate seeded = new(240, TimeSpan.FromSeconds(5));
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);
        context.MinResponseDataRate.ShouldBeNull();

        // Act
        context.MinResponseDataRate = seeded;
        context.FreezeMinRequestBodyDataRate();
        context.MinResponseDataRate = null;
        context.FreezeMinResponseDataRate();
        context.FreezeMinResponseDataRate();

        // Assert — the request-body freeze leaves the response knob writable; its own freeze does not.
        context.IsMinResponseDataRateReadOnly.ShouldBeTrue();
        context.MinResponseDataRate.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => context.MinResponseDataRate = seeded);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: No response interceptor is added to an exchange by default")]
    public void Context_ResponseInterceptors_ShouldStartEmpty()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);

        context.ResponseInterceptors.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: The extended CONNECT protocol is optional and defaults to null")]
    public void Context_Protocol_ShouldDefaultToNull()
    {
        // Arrange / Act — a construction site that predates the member still compiles (#1368).
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);
        HttpExchangeInterceptorRequestContext extendedConnect = new()
        {
            Version = HttpVersion.Http20,
            Method = HttpMethod.Connect,
            Path = new HttpPath("/chat"),
            Scheme = HttpScheme.Https,
            Host = new HttpHost("api.test"),
            Protocol = "websocket",
            Headers = new HttpHeaderCollection().AsReadOnly(),
            Features = new HttpFeatureCollection(),
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = null,
        };

        // Assert
        context.Protocol.ShouldBeNull();
        extendedConnect.Protocol.ShouldBe("websocket");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Added response interceptors keep their order and are added once each")]
    public void Context_AddResponseInterceptor_ShouldKeepOrderAndIgnoreDuplicates()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);
        IHttpExchangeInterceptor first = new NoOverrideInterceptor();
        IHttpExchangeInterceptor second = new NoOverrideInterceptor();

        context.AddResponseInterceptor(first);
        context.AddResponseInterceptor(second);
        context.AddResponseInterceptor(first);

        context.ResponseInterceptors.ShouldBe(new[] { first, second });
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Adding a null response interceptor throws")]
    public void Context_AddResponseInterceptor_OnNull_ShouldThrow()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);

        Should.Throw<ArgumentNullException>(() => context.AddResponseInterceptor(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - InterceptorContext: Equal distinct response interceptors preserve the original instances before and after promotion")]
    public void AddResponseInterceptor_OnEqualDistinctInstances_ShouldKeepOriginalsBeforeAndAfterPromotion()
    {
        // Arrange
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);
        IHttpExchangeInterceptor first = new EqualInterceptor(1);
        IHttpExchangeInterceptor second = new EqualInterceptor(2);

        // Act - singleton deduplication uses the interceptor's equality, not its identity.
        context.AddResponseInterceptor(first);
        context.AddResponseInterceptor(new EqualInterceptor(1));

        // Assert
        context.ResponseInterceptors.Count.ShouldBe(1);
        context.ResponseInterceptors[0].ShouldBeSameAs(first);

        // Act - a distinct interceptor promotes storage while preserving the same equality rule.
        context.AddResponseInterceptor(second);
        context.AddResponseInterceptor(new EqualInterceptor(1));
        context.AddResponseInterceptor(new EqualInterceptor(2));

        // Assert
        context.ResponseInterceptors.Count.ShouldBe(2);
        context.ResponseInterceptors[0].ShouldBeSameAs(first);
        context.ResponseInterceptors[1].ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Interceptor: The base class virtual defaults should no-op and pass the body stream through")]
    public void Interceptor_Defaults_ShouldPassThrough()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);
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

    private static HttpExchangeInterceptorRequestContext CreateContext(long? maxRequestBodySize)
    {
        return new HttpExchangeInterceptorRequestContext
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

    private sealed class EqualInterceptor : HttpExchangeInterceptor
    {
        private readonly int _key;

        public EqualInterceptor(int key)
        {
            _key = key;
        }

        public override bool Equals(object? obj) => obj is EqualInterceptor other && _key == other._key;

        public override int GetHashCode() => _key;
    }
}
