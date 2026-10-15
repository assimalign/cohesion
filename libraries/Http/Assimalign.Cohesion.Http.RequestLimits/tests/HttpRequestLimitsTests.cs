using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.RequestLimits.Tests;

using Assimalign.Cohesion.Http;

public class HttpRequestLimitsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Interceptor: Head hook should attach the typed feature")]
    public void AfterRequestHead_ShouldAttachFeature()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);
        HttpMaxRequestBodySizeInterceptor interceptor = new HttpMaxRequestBodySizeInterceptor();

        interceptor.AfterRequestHead(context);

        IHttpMaxRequestBodySizeFeature? feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        feature.ShouldNotBeNull();
        feature!.MaxRequestBodySize.ShouldBe(1024);
        feature.IsReadOnly.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Feature: Should write through to the context knob the transport enforces")]
    public void Feature_ShouldWriteThroughToContext()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);
        new HttpMaxRequestBodySizeInterceptor().AfterRequestHead(context);
        IHttpMaxRequestBodySizeFeature feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;

        // Feature write is visible on the context (the enforced value)…
        feature.MaxRequestBodySize = 4096;
        context.MaxRequestBodySize.ShouldBe(4096);

        // …and a context write (e.g. by a later head hook) is visible on the feature.
        context.MaxRequestBodySize = null;
        feature.MaxRequestBodySize.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Feature: Read-only lifecycle should delegate to the transport-owned freeze")]
    public void Feature_ReadOnly_ShouldDelegateToContextFreeze()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);
        new HttpMaxRequestBodySizeInterceptor().AfterRequestHead(context);
        IHttpMaxRequestBodySizeFeature feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;

        context.FreezeMaxRequestBodySize();

        feature.IsReadOnly.ShouldBeTrue();
        feature.MaxRequestBodySize.ShouldBe(1024); // observation always works
        Should.Throw<InvalidOperationException>(() => feature.MaxRequestBodySize = 5);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Feature: Should reject a negative per-request cap")]
    public void Feature_OnNegative_ShouldThrow()
    {
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);
        new HttpMaxRequestBodySizeInterceptor().AfterRequestHead(context);
        IHttpMaxRequestBodySizeFeature feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;

        Should.Throw<ArgumentOutOfRangeException>(() => feature.MaxRequestBodySize = -1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Limits interceptor: Should attach one feature carrying the body-size cap and the request-body data rate")]
    public void AfterRequestHead_CombinedLimits_ShouldAttachOneFeatureForEveryLimit()
    {
        // Arrange
        HttpMinDataRate seeded = new(240, TimeSpan.FromSeconds(5));
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);
        context.MinRequestBodyDataRate = seeded;

        // Act
        HttpRequestLimitsInterceptor interceptor = new HttpRequestLimitsInterceptor();
        interceptor.AfterRequestHead(context);

        // Assert — one object, under the body-size feature's historical name, found by either contract.
        IHttpMaxRequestBodySizeFeature? size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        IHttpMinRequestBodyDataRateFeature? rate = context.Features.Get<IHttpMinRequestBodyDataRateFeature>();
        size.ShouldNotBeNull();
        rate.ShouldNotBeNull();
        rate.ShouldBeSameAs(size);
        context.Features.Count().ShouldBe(1);
        context.Features.Get("Assimalign.Cohesion.Http.MaxRequestBodySize").ShouldBeSameAs(size);
        size!.MaxRequestBodySize.ShouldBe(1024);
        rate!.MinDataRate.ShouldBeSameAs(seeded);
        rate.IsReadOnly.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Rate feature: Should write through to the context knob the transport enforces")]
    public void MinRequestBodyDataRate_OnWrite_ShouldWriteThroughToContext()
    {
        // Arrange
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);
        context.MinRequestBodyDataRate = new HttpMinDataRate(240, TimeSpan.FromSeconds(5));
        new HttpRequestLimitsInterceptor().AfterRequestHead(context);
        IHttpMinRequestBodyDataRateFeature feature = context.Features.Get<IHttpMinRequestBodyDataRateFeature>()!;
        HttpMinDataRate raised = new(4096, TimeSpan.FromSeconds(1));

        // Act and assert — a feature write is what the transport reads, and a context write shows on the feature.
        feature.MinDataRate = null;
        context.MinRequestBodyDataRate.ShouldBeNull();
        context.MinRequestBodyDataRate = raised;
        feature.MinDataRate.ShouldBeSameAs(raised);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Rate feature: Read-only lifecycle should follow the rate's own freeze")]
    public void MinRequestBodyDataRate_OnFreeze_ShouldBecomeReadOnly()
    {
        // Arrange
        HttpMinDataRate seeded = new(240, TimeSpan.FromSeconds(5));
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: 1024);
        context.MinRequestBodyDataRate = seeded;
        new HttpRequestLimitsInterceptor().AfterRequestHead(context);
        IHttpMinRequestBodyDataRateFeature rate = context.Features.Get<IHttpMinRequestBodyDataRateFeature>()!;
        IHttpMaxRequestBodySizeFeature size = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;

        // Act — HTTP/2 freezes the cap at dispatch but the rate only at the first body read.
        context.FreezeMaxRequestBodySize();

        // Assert
        size.IsReadOnly.ShouldBeTrue();
        rate.IsReadOnly.ShouldBeFalse();
        rate.MinDataRate = null;

        context.FreezeMinRequestBodyDataRate();
        rate.IsReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => rate.MinDataRate = seeded);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Response rate feature: Should write through to the context knob and follow its freeze")]
    public void MinResponseDataRate_OnWriteAndFreeze_ShouldWriteThroughAndBecomeReadOnly()
    {
        // Arrange
        HttpMinDataRate seeded = new(240, TimeSpan.FromSeconds(5));
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);
        context.MinResponseDataRate = seeded;
        new HttpRequestLimitsInterceptor().AfterRequestHead(context);
        IHttpMinResponseDataRateFeature? feature = context.Features.Get<IHttpMinResponseDataRateFeature>();

        // Act and assert — the same object as the other limits, viewing the response knob.
        feature.ShouldNotBeNull();
        feature.ShouldBeSameAs(context.Features.Get<IHttpMaxRequestBodySizeFeature>());
        feature!.MinDataRate.ShouldBeSameAs(seeded);
        feature.MinDataRate = null;
        context.MinResponseDataRate.ShouldBeNull();

        // The request-body freeze does not touch the response knob; the response's own freeze does.
        context.FreezeMinRequestBodyDataRate();
        feature.IsReadOnly.ShouldBeFalse();
        context.FreezeMinResponseDataRate();
        feature.IsReadOnly.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => feature.MinDataRate = seeded);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Context extension: Should surface the response data-rate feature")]
    public void MinResponseDataRateExtension_OnLimitsInterceptor_ShouldReturnFeature()
    {
        // Arrange
        HttpExchangeInterceptorRequestContext parse = CreateContext(maxRequestBodySize: null);
        new HttpRequestLimitsInterceptor().AfterRequestHead(parse);
        HttpContextDouble exchange = new(parse.Features);

        // Act
        IHttpMinResponseDataRateFeature? feature = exchange.MinResponseDataRate;

        // Assert
        feature.ShouldBeSameAs(parse.Features.Get<IHttpMinResponseDataRateFeature>());
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Context extension: Should surface the request-body data-rate feature")]
    public void MinRequestBodyDataRateExtension_OnLimitsInterceptor_ShouldReturnFeature()
    {
        // Arrange
        HttpExchangeInterceptorRequestContext parse = CreateContext(maxRequestBodySize: null);
        new HttpRequestLimitsInterceptor().AfterRequestHead(parse);
        HttpContextDouble exchange = new(parse.Features);

        // Act
        IHttpMinRequestBodyDataRateFeature? feature = exchange.MinRequestBodyDataRate;

        // Assert
        feature.ShouldBeSameAs(parse.Features.Get<IHttpMinRequestBodyDataRateFeature>());
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Interceptor: Should chain body-stream wrapping in registration order")]
    public void BodyHooks_ShouldChainWrappersInRegistrationOrder()
    {
        // Demonstrates the stream-override capability of the seam: each interceptor receives the
        // previous result, so the last registered ends up outermost.
        HttpExchangeInterceptorRequestContext context = CreateContext(maxRequestBodySize: null);
        IHttpExchangeInterceptor limits = new HttpMaxRequestBodySizeInterceptor();
        IHttpExchangeInterceptor wrapper = new ReadOnlyWrappingInterceptor();

        using MemoryStream original = new(new byte[] { 1, 2, 3 });
        Stream result = original;

        foreach (IHttpExchangeInterceptor interceptor in new[] { limits, wrapper })
        {
            interceptor.AfterRequestHead(context);
            result = interceptor.AfterRequestBody(context, result);
        }

        // The limits interceptor passes through (DIM default); the wrapper decorates.
        ReadOnlyStream wrapped = result.ShouldBeOfType<ReadOnlyStream>();
        wrapped.CanWrite.ShouldBeFalse();
        byte[] buffer = new byte[3];
        wrapped.Read(buffer, 0, 3).ShouldBe(3);
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
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

    /// <summary>
    /// An exchange that only carries features: the extension members read nothing else.
    /// </summary>
    private sealed class HttpContextDouble : IHttpContext
    {
        public HttpContextDouble(IHttpFeatureCollection features)
        {
            Features = features;
        }

        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request => throw new NotSupportedException();
        public IHttpResponse Response => throw new NotSupportedException();
        public IHttpConnectionInfo ConnectionInfo => HttpConnectionInfo.Empty;
        public IHttpFeatureCollection Features { get; }
        public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
        public CancellationToken RequestCancelled => CancellationToken.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Cancel() { }
        public Task CancelAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Sample stream-override interceptor: wraps the request body in a read-only decorator —
    /// the "readonly stream wrapper" scenario the seam exists to enable.
    /// </summary>
    private sealed class ReadOnlyWrappingInterceptor : HttpExchangeInterceptor
    {
        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            return new ReadOnlyStream(body);
        }
    }

    private sealed class ReadOnlyStream : Stream
    {
        private readonly Stream _inner;

        public ReadOnlyStream(Stream inner)
        {
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // A wrapper owns the stream it wraps (see IHttpExchangeInterceptor docs).
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
