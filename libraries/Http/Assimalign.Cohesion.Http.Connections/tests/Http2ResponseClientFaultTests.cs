using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Response-client faults are visible through the exchange control before cancellation wakes a
/// streamed handler, including stalls found by the shared output rather than the handler's writer.
/// </summary>
public class Http2ResponseClientFaultTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpMinDataRate _rate = new(1_000_000, TimeSpan.FromMilliseconds(300));

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Client Fault: A slow response should latch before cancellation and include every response a connection stall ends")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_OnFlowControlStall_ShouldLatchBeforeCancellation(bool connectionStall)
    {
        // Arrange — both responses have begun, but the peer will grant no further DATA credit.
        CaptureControlInterceptor capture = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            CreateOptions(capture), http2 => http2.Limits.MinResponseDataRate = _rate,
            initialWindowSize: connectionStall ? 1u << 20 : 0);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/stalled"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = capture.Control.ShouldNotBeNull();
        await exchange.Response.Streaming.FlushAsync(CancellationToken.None);
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/other"));
        IHttpContext other = await peer.ReceiveContextAsync();
        IHttpExchangeControl otherControl = capture.Control.ShouldNotBeNull();
        await other.Response.Streaming.FlushAsync(CancellationToken.None);
        TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = exchange.RequestCancelled.Register(
            () => cancelled.TrySetResult(control.ResponseAbortedByClient));
        TaskCompletionSource<bool> otherCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration otherRegistration = other.RequestCancelled.Register(
            () => otherCancelled.TrySetResult(otherControl.ResponseAbortedByClient));

        // Act
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(
            () => exchange.Response.Streaming.WriteAsync(new byte[100 * 1024], CancellationToken.None).AsTask().WaitAsync(_timeout));

        // Assert
        (await cancelled.Task.WaitAsync(_timeout)).ShouldBeTrue();
        control.ResponseAbortedByClient.ShouldBeTrue();
        control.ClientFaultStatusCode.ShouldBeNull();
        failure.CancellationToken.ShouldBe(exchange.RequestCancelled);
        if (connectionStall)
        {
            (await otherCancelled.Task.WaitAsync(_timeout)).ShouldBeTrue();
            otherControl.ResponseAbortedByClient.ShouldBeTrue();
            await peer.WaitForReceiveEndAsync();
        }
        else
        {
            otherControl.ResponseAbortedByClient.ShouldBeFalse();
            other.RequestCancelled.IsCancellationRequested.ShouldBeFalse();
            await peer.ConnectionContext.SendAsync(other, CancellationToken.None).AsTask().WaitAsync(_timeout);
            await peer.ConnectionContext.SendAsync(exchange, CancellationToken.None).AsTask().WaitAsync(_timeout);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Client Fault: A peer reset mid-response should latch before cancellation even when the writer returns silently")]
    public async Task WriteAsync_OnPeerResetMidResponse_ShouldLatchBeforeCancellation()
    {
        // Arrange
        CaptureControlInterceptor capture = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(capture), initialWindowSize: 10);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/stream"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = capture.Control.ShouldNotBeNull();
        TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = exchange.RequestCancelled.Register(
            () => cancelled.TrySetResult(control.ResponseAbortedByClient));
        Task write = exchange.Response.Streaming.WriteAsync(new byte[100], CancellationToken.None).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) == 10, "the partial response");

        // Act
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
        await write.WaitAsync(_timeout);

        // Assert
        (await cancelled.Task.WaitAsync(_timeout)).ShouldBeTrue();
        control.ResponseAbortedByClient.ShouldBeTrue();
        control.ClientFaultStatusCode.ShouldBeNull();
        await peer.ConnectionContext.SendAsync(exchange, CancellationToken.None).AsTask().WaitAsync(_timeout);
        control.ResponseAbortedByClient.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Client Fault: A stalled connection output should latch every active response before cancellation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_OnConnectionOutputStall_ShouldLatchEveryResponse(bool controlFrameStall)
    {
        // Arrange — one response is writing, another is between writes when the output stops draining.
        CaptureControlInterceptor capture = new();
        StallingConnection? transport = null;
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            CreateOptions(capture), http2 => http2.Limits.MinResponseDataRate = _rate,
            decorate: inner => transport = new StallingConnection(inner));
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/stream"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = capture.Control.ShouldNotBeNull();
        await exchange.Response.Streaming.WriteAsync(new byte[1], CancellationToken.None);
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/other"));
        IHttpContext other = await peer.ReceiveContextAsync();
        IHttpExchangeControl otherControl = capture.Control.ShouldNotBeNull();
        await other.Response.Streaming.WriteAsync(new byte[1], CancellationToken.None);
        await peer.SyncAsync();
        TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = exchange.RequestCancelled.Register(
            () => cancelled.TrySetResult(control.ResponseAbortedByClient));
        TaskCompletionSource<bool> otherCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration otherRegistration = other.RequestCancelled.Register(
            () => otherCancelled.TrySetResult(otherControl.ResponseAbortedByClient));

        // Act — a PING acknowledgement can be the stalled writer, without any response write in flight.
        transport!.Writer.Stall();
        if (controlFrameStall)
        {
            await peer.SendAsync(Http2TestSettings.RawFrame(0x6, 0, 0, new byte[8]));
        }
        else
        {
            await Should.ThrowAsync<OperationCanceledException>(
                () => exchange.Response.Streaming.WriteAsync(new byte[1], CancellationToken.None).AsTask().WaitAsync(_timeout));
        }

        // Assert
        (await cancelled.Task.WaitAsync(_timeout)).ShouldBeTrue();
        (await otherCancelled.Task.WaitAsync(_timeout)).ShouldBeTrue();
        control.ResponseAbortedByClient.ShouldBeTrue();
        otherControl.ResponseAbortedByClient.ShouldBeTrue();
        control.ClientFaultStatusCode.ShouldBeNull();
        otherControl.ClientFaultStatusCode.ShouldBeNull();
        transport.IsAborted.ShouldBeTrue();
        await peer.WaitForReceiveEndAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response Client Fault: Local cancellation and a reset before any response should leave the response-client latch clear")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_OnCancellationWithoutPeerAbortingResponse_ShouldLeaveLatchClear(bool peerResetBeforeResponse)
    {
        // Arrange — these cases pin existing classification rather than reproduce the missing latch.
        CaptureControlInterceptor capture = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(CreateOptions(capture));
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/cancelled"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = capture.Control.ShouldNotBeNull();

        // Act
        if (peerResetBeforeResponse)
        {
            await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
            await peer.SyncAsync();
        }
        else
        {
            await exchange.Response.Streaming.WriteAsync(new byte[1], CancellationToken.None);
            exchange.Cancel();
        }
        await peer.ConnectionContext.SendAsync(exchange, CancellationToken.None).AsTask().WaitAsync(_timeout);

        // Assert
        control.ResponseAbortedByClient.ShouldBeFalse();
        control.ClientFaultStatusCode.ShouldBeNull();
    }

    private static HttpConnectionListenerOptions CreateOptions(CaptureControlInterceptor capture)
    {
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        options.Interceptors.Add(capture);
        return options;
    }

    private sealed class CaptureControlInterceptor : HttpExchangeInterceptor
    {
        public IHttpExchangeControl? Control { get; private set; }

        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
        {
            Control = context.Control;
        }
    }
}
