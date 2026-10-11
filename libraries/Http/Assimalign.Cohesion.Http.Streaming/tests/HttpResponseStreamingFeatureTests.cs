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
    public void Interceptor_BeforeResponse_ShouldInstallStreamingFeature()
    {
        RecordingSink sink = new();
        HttpExchangeInterceptorResponseContext context = CreateContext(sink);

        HttpResponseStreaming.CreateInterceptor().BeforeResponse(context);

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

    [Theory(DisplayName = "Cohesion Test [Http.Streaming] - Feature: A failed first sink operation without exchange control leaves the feature unstarted and retryable")]
    [InlineData("StartAsync")]
    [InlineData("WriteAsync")]
    [InlineData("FlushAsync")]
    [InlineData("CompleteAsync")]
    public async Task StreamingOperation_FirstSinkFailureWithoutControl_ShouldRemainUnstartedAndPermitRetry(string operation)
    {
        // Arrange — hand-built contexts may omit the optional transport control.
        IOException expected = new("The sink refused its first operation.");
        using RecordingSink sink = new() { NextFailure = expected };
        HttpExchangeInterceptorResponseContext context = CreateContext(sink);
        HttpResponseStreaming.CreateInterceptor().BeforeResponse(context);
        IHttpResponseStreamingFeature feature = context.Features.Get<IHttpResponseStreamingFeature>()!;

        // Act
        IOException failure = await Should.ThrowAsync<IOException>(
            () => InvokeOperationAsync(feature, operation, CancellationToken.None).AsTask());
        bool startedAfterFailure = feature.HasStarted;
        await InvokeOperationAsync(feature, operation, CancellationToken.None);

        // Assert
        failure.ShouldBeSameAs(expected);
        startedAfterFailure.ShouldBeFalse();
        feature.HasStarted.ShouldBeTrue();
        sink.OperationCount.ShouldBe(2);
        if (operation == "WriteAsync")
        {
            Encoding.UTF8.GetString(sink.Written).ShouldBe("body");
        }
        else
        {
            sink.FlushCount.ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Feature: A failed completion permits a write and another completion before enforcing the completion guard")]
    public async Task CompleteAsync_FirstSinkFailure_ShouldAllowWriteAndRetryThenForbidFurtherWrites()
    {
        // Arrange
        using RecordingSink sink = new() { NextFailure = new IOException("The sink refused completion.") };
        HttpExchangeInterceptorResponseContext context = CreateContext(sink);
        HttpResponseStreaming.CreateInterceptor().BeforeResponse(context);
        IHttpResponseStreamingFeature feature = context.Features.Get<IHttpResponseStreamingFeature>()!;

        // Act
        await Should.ThrowAsync<IOException>(() => feature.CompleteAsync(CancellationToken.None).AsTask());
        bool startedAfterFailure = feature.HasStarted;
        await feature.WriteAsync(Encoding.UTF8.GetBytes("after failure"), CancellationToken.None);
        await feature.CompleteAsync(CancellationToken.None);
        await feature.CompleteAsync(CancellationToken.None);
        await Should.ThrowAsync<InvalidOperationException>(
            () => feature.WriteAsync(Encoding.UTF8.GetBytes("after completion"), CancellationToken.None).AsTask());
        await Should.ThrowAsync<InvalidOperationException>(() => feature.FlushAsync(CancellationToken.None).AsTask());

        // Assert
        startedAfterFailure.ShouldBeFalse();
        feature.HasStarted.ShouldBeTrue();
        Encoding.UTF8.GetString(sink.Written).ShouldBe("after failure");
        sink.OperationCount.ShouldBe(3);
        sink.FlushCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Streaming] - Feature: A sink failure after the transport commits the head must still report started")]
    [InlineData("StartAsync")]
    [InlineData("WriteAsync")]
    [InlineData("FlushAsync")]
    [InlineData("CompleteAsync")]
    public async Task StreamingOperation_SinkFailsAfterHeadCommit_ShouldReportTransportStarted(string operation)
    {
        // Arrange — the head can already be on the wire when a later flush or body write fails.
        FakeExchangeControl control = new();
        using RecordingSink sink = new()
        {
            NextFailure = new IOException("The sink failed after committing the head."),
            BeforeFailure = () => control.HasResponseStarted = true,
        };
        HttpExchangeInterceptorResponseContext context = CreateContext(sink, control);
        HttpResponseStreaming.CreateInterceptor().BeforeResponse(context);
        IHttpResponseStreamingFeature feature = context.Features.Get<IHttpResponseStreamingFeature>()!;

        // Act
        await Should.ThrowAsync<IOException>(
            () => InvokeOperationAsync(feature, operation, CancellationToken.None).AsTask());
        bool startedAfterFailure = feature.HasStarted;
        await feature.StartAsync(CancellationToken.None);

        // Assert — this pins the already-started behavior while the refused-head case is corrected.
        startedAfterFailure.ShouldBeTrue();
        feature.HasStarted.ShouldBeTrue();
        sink.OperationCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Feature: A head committed through another transport path is observed without flushing the streaming sink")]
    public async Task HasStarted_TransportStartsOutsideFeature_ShouldObserveCommitAndSkipStart()
    {
        // Arrange
        FakeExchangeControl control = new();
        using RecordingSink sink = new();
        HttpExchangeInterceptorResponseContext context = CreateContext(sink, control);
        HttpResponseStreaming.CreateInterceptor().BeforeResponse(context);
        IHttpResponseStreamingFeature feature = context.Features.Get<IHttpResponseStreamingFeature>()!;
        feature.HasStarted.ShouldBeFalse();

        // Act — a buffered send or another response feature commits the same exchange.
        control.HasResponseStarted = true;
        bool startedBeforeCallingFeature = feature.HasStarted;
        await feature.StartAsync(CancellationToken.None);

        // Assert
        startedBeforeCallingFeature.ShouldBeTrue();
        feature.HasStarted.ShouldBeTrue();
        sink.OperationCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Streaming] - Accessor: Response.Streaming resolves the installed feature")]
    public void ResponseStreamingAccessor_ShouldResolveInstalledFeature()
    {
        RecordingSink sink = new();
        HttpExchangeInterceptorResponseContext context = CreateContext(sink);
        HttpResponseStreaming.CreateInterceptor().BeforeResponse(context);

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
        HttpExchangeInterceptorResponseContext context = CreateContext(sink);
        HttpResponseStreaming.CreateInterceptor().BeforeResponse(context);
        return (context.Features.Get<IHttpResponseStreamingFeature>()!, sink);
    }

    private static ValueTask InvokeOperationAsync(IHttpResponseStreamingFeature feature, string operation, CancellationToken cancellationToken)
        => operation switch
        {
            "StartAsync" => feature.StartAsync(cancellationToken),
            "WriteAsync" => feature.WriteAsync(Encoding.UTF8.GetBytes("body"), cancellationToken),
            "FlushAsync" => feature.FlushAsync(cancellationToken),
            "CompleteAsync" => feature.CompleteAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static HttpExchangeInterceptorResponseContext CreateContext(Stream sink, IHttpExchangeControl? control = null) => new()
    {
        Version = HttpVersion.Http11,
        Headers = new HttpHeaderCollection(),
        Features = new HttpFeatureCollection(),
        ConnectionInfo = new HttpConnectionInfo(),
        ResponseBody = sink,
        Control = control,
    };

    /// <summary>A write-only stream that records the bytes and flushes written through the feature.</summary>
    private sealed class RecordingSink : Stream
    {
        private readonly List<byte> _written = new();

        public byte[] Written => _written.ToArray();
        public int FlushCount { get; private set; }
        public int OperationCount { get; private set; }
        public IOException? NextFailure { get; set; }
        public Action? BeforeFailure { get; set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BeginOperation();
            _written.AddRange(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            BeginOperation();
            FlushCount++;
            return Task.CompletedTask;
        }

        public override void Flush() => FlushCount++;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _written.AddRange(new ReadOnlySpan<byte>(buffer, offset, count).ToArray());

        private void BeginOperation()
        {
            OperationCount++;
            if (NextFailure is { } failure)
            {
                NextFailure = null;
                BeforeFailure?.Invoke();
                throw failure;
            }
        }
    }

    /// <summary>Publishes the transport's head-commit state without implementing unrelated wire actions.</summary>
    private sealed class FakeExchangeControl : IHttpExchangeControl
    {
        public bool HasResponseStarted { get; set; }
        public bool CanWriteInterimResponse => !HasResponseStarted;
        public bool CanTakeOver => false;
        public bool CanAcceptTunnel => false;

        public ValueTask WriteInterimResponseAsync(HttpStatusCode statusCode, IHttpHeaderCollection? headers = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Stream TakeOver() => throw new NotSupportedException();

        public ValueTask<Stream> AcceptTunnelAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
