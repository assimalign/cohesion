using System;
using System.IO;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.RequestLimits.Tests;

using Assimalign.Cohesion.Http;

public class HttpRequestLimitsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Interceptor: Head hook should attach the typed feature")]
    public void AfterRequestHead_ShouldAttachFeature()
    {
        HttpRequestInterceptorContext context = CreateContext(maxRequestBodySize: 1024);
        IHttpRequestInterceptor interceptor = HttpRequestLimits.CreateMaxRequestBodySizeInterceptor();

        interceptor.AfterRequestHead(context);

        IHttpMaxRequestBodySizeFeature? feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        feature.ShouldNotBeNull();
        feature!.MaxRequestBodySize.ShouldBe(1024);
        feature.IsReadOnly.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Feature: Should write through to the context knob the transport enforces")]
    public void Feature_ShouldWriteThroughToContext()
    {
        HttpRequestInterceptorContext context = CreateContext(maxRequestBodySize: 1024);
        HttpRequestLimits.CreateMaxRequestBodySizeInterceptor().AfterRequestHead(context);
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
        HttpRequestInterceptorContext context = CreateContext(maxRequestBodySize: 1024);
        HttpRequestLimits.CreateMaxRequestBodySizeInterceptor().AfterRequestHead(context);
        IHttpMaxRequestBodySizeFeature feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;

        context.FreezeMaxRequestBodySize();

        feature.IsReadOnly.ShouldBeTrue();
        feature.MaxRequestBodySize.ShouldBe(1024); // observation always works
        Should.Throw<InvalidOperationException>(() => feature.MaxRequestBodySize = 5);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Feature: Should reject a negative per-request cap")]
    public void Feature_OnNegative_ShouldThrow()
    {
        HttpRequestInterceptorContext context = CreateContext(maxRequestBodySize: 1024);
        HttpRequestLimits.CreateMaxRequestBodySizeInterceptor().AfterRequestHead(context);
        IHttpMaxRequestBodySizeFeature feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>()!;

        Should.Throw<ArgumentOutOfRangeException>(() => feature.MaxRequestBodySize = -1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.RequestLimits] - Interceptor: Should chain body-stream wrapping in registration order")]
    public void BodyHooks_ShouldChainWrappersInRegistrationOrder()
    {
        // Demonstrates the stream-override capability of the seam: each interceptor receives the
        // previous result, so the last registered ends up outermost.
        HttpRequestInterceptorContext context = CreateContext(maxRequestBodySize: null);
        IHttpRequestInterceptor limits = HttpRequestLimits.CreateMaxRequestBodySizeInterceptor();
        IHttpRequestInterceptor wrapper = new ReadOnlyWrappingInterceptor();

        using MemoryStream original = new(new byte[] { 1, 2, 3 });
        Stream result = original;

        foreach (IHttpRequestInterceptor interceptor in new[] { limits, wrapper })
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

    /// <summary>
    /// Sample stream-override interceptor: wraps the request body in a read-only decorator —
    /// the "readonly stream wrapper" scenario the seam exists to enable.
    /// </summary>
    private sealed class ReadOnlyWrappingInterceptor : IHttpRequestInterceptor
    {
        public Stream AfterRequestBody(HttpRequestInterceptorContext context, Stream body)
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
            // A wrapper owns the stream it wraps (see IHttpRequestInterceptor docs).
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
