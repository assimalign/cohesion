using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Streaming.Tests;

public class HttpResponseStreamingFeatureTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Interceptor: Should install a streaming feature over the response body sink")]
    public void Interceptor_OnResponse_ShouldInstallStreamingFeature()
    {
        RecordingSink sink = new();
        HttpResponseInterceptorContext context = CreateContext(sink);

        HttpResponseStreaming.CreateInterceptor().OnResponse(context);

        context.Features.Get<IHttpResponseStreamingFeature>().ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Feature: WriteAsync forwards bytes to the sink and marks started")]
    public async Task WriteAsync_ShouldForwardToSinkAndStart()
    {
        (IHttpResponseStreamingFeature feature, RecordingSink sink) = CreateFeature();

        feature.HasStarted.ShouldBeFalse();
        await feature.WriteAsync(Encoding.UTF8.GetBytes("hello"));
        await feature.WriteAsync(Encoding.UTF8.GetBytes(" world"));

        feature.HasStarted.ShouldBeTrue();
        Encoding.UTF8.GetString(sink.Written).ShouldBe("hello world");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Feature: StartAsync commits the head via a flush and is idempotent")]
    public async Task StartAsync_ShouldFlushOnceAndStart()
    {
        (IHttpResponseStreamingFeature feature, RecordingSink sink) = CreateFeature();

        await feature.StartAsync();
        await feature.StartAsync();

        feature.HasStarted.ShouldBeTrue();
        sink.FlushCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Feature: FlushAsync starts the response and flushes")]
    public async Task FlushAsync_ShouldStartAndFlush()
    {
        (IHttpResponseStreamingFeature feature, RecordingSink sink) = CreateFeature();

        await feature.FlushAsync();

        feature.HasStarted.ShouldBeTrue();
        sink.FlushCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Feature: CompleteAsync is idempotent")]
    public async Task CompleteAsync_CalledTwice_ShouldFlushOnce()
    {
        (IHttpResponseStreamingFeature feature, RecordingSink sink) = CreateFeature();

        await feature.WriteAsync(Encoding.UTF8.GetBytes("x"));
        await feature.CompleteAsync();
        await feature.CompleteAsync();

        sink.FlushCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Feature: Writing after completion throws")]
    public async Task WriteAsync_AfterComplete_ShouldThrow()
    {
        (IHttpResponseStreamingFeature feature, RecordingSink _) = CreateFeature();

        await feature.WriteAsync(Encoding.UTF8.GetBytes("x"));
        await feature.CompleteAsync();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await feature.WriteAsync(Encoding.UTF8.GetBytes("y")));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Accessor: Response.Streaming resolves the installed feature")]
    public void ResponseStreamingAccessor_ShouldResolveInstalledFeature()
    {
        RecordingSink sink = new();
        HttpResponseInterceptorContext context = CreateContext(sink);
        HttpResponseStreaming.CreateInterceptor().OnResponse(context);

        FakeHttpResponse response = new(context.Features);

        response.SupportsStreaming.ShouldBeTrue();
        response.Streaming.ShouldBeSameAs(context.Features.Get<IHttpResponseStreamingFeature>());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Accessor: Response.Streaming throws when streaming is not enabled")]
    public void ResponseStreamingAccessor_WhenNotEnabled_ShouldThrow()
    {
        FakeHttpResponse response = new(new HttpFeatureCollection());

        response.SupportsStreaming.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => _ = response.Streaming);
    }

    private static (IHttpResponseStreamingFeature Feature, RecordingSink Sink) CreateFeature()
    {
        RecordingSink sink = new();
        HttpResponseInterceptorContext context = CreateContext(sink);
        HttpResponseStreaming.CreateInterceptor().OnResponse(context);
        return (context.Features.Get<IHttpResponseStreamingFeature>()!, sink);
    }

    private static HttpResponseInterceptorContext CreateContext(Stream sink) => new()
    {
        Version = HttpVersion.Http11,
        Headers = new HttpHeaderCollection(),
        Features = new HttpFeatureCollection(),
        ConnectionInfo = new HttpConnectionInfo(),
        ResponseBody = sink,
    };

    /// <summary>A write-only stream that records the bytes and flushes written through the feature.</summary>
    private sealed class RecordingSink : Stream
    {
        private readonly List<byte> _written = new();

        public byte[] Written => _written.ToArray();
        public int FlushCount { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _written.AddRange(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }

        public override void Flush() => FlushCount++;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _written.AddRange(new ReadOnlySpan<byte>(buffer, offset, count).ToArray());
    }

    /// <summary>A minimal <see cref="IHttpResponse"/> exposing a feature collection, for the accessor tests.</summary>
    private sealed class FakeHttpResponse : IHttpResponse, IHttpContext
    {
        public FakeHttpResponse(IHttpFeatureCollection features)
        {
            Features = features;
        }

        // IHttpResponse
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.Ok;
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpContext HttpContext => this;
        public Stream Body { get; set; } = Stream.Null;

        // IHttpContext (only Features is exercised by the accessor)
        public HttpVersion Version => HttpVersion.Http11;
        public IHttpRequest Request => throw new NotSupportedException();
        public IHttpResponse Response => this;
        public IHttpConnectionInfo ConnectionInfo => throw new NotSupportedException();
        public IHttpFeatureCollection Features { get; }
        public IDictionary<string, object?> Items => throw new NotSupportedException();
        public CancellationToken RequestCancelled => CancellationToken.None;
        public void Cancel() { }
        public Task CancelAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
