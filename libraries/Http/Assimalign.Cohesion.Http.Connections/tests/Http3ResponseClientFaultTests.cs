using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Response-side client faults remain readable after HTTP/3 response writes fail (#1432).</summary>
public class Http3ResponseClientFaultTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Client Fault: A slow reader should latch before the streamed write fails")]
    public async Task ResponseAbortedByClient_OnSlowStreamedResponse_ShouldLatchAndCancel()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        StallingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MinResponseDataRate = new HttpMinDataRate(1_000_000, TimeSpan.FromMilliseconds(100)),
            ConfigureInterceptors,
            inner => server = new StallingMultiplexedConnection(inner));
        await using Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/stream", "https", "a"), timeout.Token);
        await request.Output.CompleteAsync();
        await using IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);
        await context.Response.Streaming.WriteAsync(new byte[1], timeout.Token);
        server.ShouldNotBeNull().RequestStreams[0].Writer.Stall();
        control.ResponseAbortedByClient.ShouldBeFalse();

        // Act
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => context.Response.Streaming.WriteAsync(new byte[1024], timeout.Token).AsTask());

        // Assert
        control.ResponseAbortedByClient.ShouldBeTrue();
        control.ClientFaultStatusCode.ShouldBeNull();
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(context.RequestCancelled);
        await peer.ConnectionContext.SendAsync(context, timeout.Token);
        control.ResponseAbortedByClient.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Client Fault: A peer reset or STOP_SENDING should latch and release a response awaiting credit")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResponseAbortedByClient_OnPeerAbortDuringWrite_ShouldLatchAndCancel(bool stopSending, bool gated)
    {
        // Arrange — leave the request's sending side open so RESET_STREAM is meaningful too.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        StallingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 => http3.Limits.MinResponseDataRate = gated ? new HttpMinDataRate(1, TimeSpan.FromMinutes(1)) : null,
            ConfigureInterceptors,
            inner => server = new StallingMultiplexedConnection(inner));
        await using Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/stream", "https", "a"), timeout.Token);
        await using IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);
        await context.Response.Streaming.WriteAsync(new byte[1], timeout.Token);
        StallingPipeWriter writer = server.ShouldNotBeNull().RequestStreams[0].Writer;
        writer.Stall();
        Task pending = context.Response.Streaming.WriteAsync(new byte[1024], timeout.Token).AsTask();
        while (!writer.IsFlushWaiting)
        {
            await Task.Delay(1, timeout.Token);
        }

        // Act
        if (stopSending)
        {
            ((IMultiplexedStreamAbort)request).AbortRead((long)Http3ErrorCode.RequestCancelled);
        }
        else
        {
            ((IMultiplexedStreamAbort)request).AbortWrite((long)Http3ErrorCode.RequestCancelled);
        }

        OperationCanceledException failure;
        try
        {
            failure = await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(timeout.Token));
        }
        finally
        {
            writer.Grant(1024 * 1024);
        }

        // Assert
        control.ResponseAbortedByClient.ShouldBeTrue();
        control.ClientFaultStatusCode.ShouldBeNull();
        failure.CancellationToken.ShouldBe(context.RequestCancelled);
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Client Fault: Application cancellation should not latch a client fault")]
    public async Task ResponseAbortedByClient_OnApplicationCancellation_ShouldRemainFalse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: ConfigureInterceptors);
        await using Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/stream", "https", "a"), timeout.Token);
        await request.Output.CompleteAsync();
        await using IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);
        await context.Response.Streaming.WriteAsync(new byte[1], timeout.Token);

        // Act
        context.Cancel();
        await peer.ConnectionContext.SendAsync(context, timeout.Token);

        // Assert — this already-held behavior guards the new response latch's classification.
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        control.ResponseAbortedByClient.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Response Client Fault: Invalid application headers should not latch a client fault")]
    public async Task ResponseAbortedByClient_OnInvalidResponseHeader_ShouldRemainFalse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: ConfigureInterceptors);
        await using Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/stream", "https", "a"), timeout.Token);
        await request.Output.CompleteAsync();
        await using IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);
        context.Response.Headers[new HttpHeaderKey("x-invalid")] = "value\r\nother: value";

        // Act
        await Should.ThrowAsync<HttpException>(() => context.Response.Streaming.WriteAsync(new byte[1], timeout.Token).AsTask());

        // Assert — this already-held behavior guards the new response latch's classification.
        control.ResponseAbortedByClient.ShouldBeFalse();
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
    }

    private static void ConfigureInterceptors(HttpConnectionListenerOptions options)
    {
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        options.Interceptors.Add(new CaptureControlInterceptor());
    }

    private static IHttpExchangeControl GetControl(IHttpContext context)
        => context.Features.Get<CapturedControlFeature>().ShouldNotBeNull().Control;

    private sealed class CaptureControlInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
        {
            context.Features.Set(new CapturedControlFeature(context.Control.ShouldNotBeNull()));
        }
    }

    private sealed class CapturedControlFeature : IHttpFeature
    {
        public CapturedControlFeature(IHttpExchangeControl control)
        {
            Control = control;
        }

        public string Name => nameof(CapturedControlFeature);

        public IHttpExchangeControl Control { get; }
    }
}
