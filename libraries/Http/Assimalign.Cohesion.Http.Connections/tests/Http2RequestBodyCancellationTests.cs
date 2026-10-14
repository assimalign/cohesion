using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// A peer that abandons an HTTP/2 request body cancels its exchange with the exact token exposed to
/// the handler, independently of the body rate policy and the token the handler passed to its read.
/// </summary>
public class Http2RequestBodyCancellationTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Body: A peer loss during a pending read should carry the exchange cancellation token")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ReadAsync_OnPeerLossWhileWaiting_ShouldCancelWithExchangeToken(
        bool connectionLoss,
        bool minimumRateEnabled,
        bool passExchangeToken)
    {
        // Arrange — the handler consumed part of the body and is waiting for the remaining DATA.
        CaptureControlInterceptor capture = new();
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(capture);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            options,
            configure: http2 => http2.Limits.MinRequestBodyDataRate = minimumRateEnabled
                ? new HttpMinDataRate(100, TimeSpan.FromMinutes(1))
                : null);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "1000")));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = capture.Control.ShouldNotBeNull();
        CancellationToken exchangeToken = exchange.RequestCancelled;
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = exchangeToken.Register(() => cancelled.TrySetResult());
        await peer.SendDataAsync(1, Http2TestPeer.CreateBody(400), endStream: false);
        await exchange.Request.Body.ReadExactlyAsync(new byte[400], CancellationToken.None).AsTask().WaitAsync(_timeout);
        Task<int> pending = exchange.Request.Body.ReadAsync(
            new byte[1000], passExchangeToken ? exchangeToken : CancellationToken.None).AsTask();
        await peer.SyncAsync();
        pending.IsCompleted.ShouldBeFalse();

        // Act — no END_STREAM completes the body, whether the peer closes the connection or resets it.
        if (connectionLoss)
        {
            peer.Transport.CompleteInput();
        }
        else
        {
            await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
        }

        // Assert — both this read and a later one report the handler's token, without a body rejection.
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(_timeout));
        await cancelled.Task.WaitAsync(_timeout);
        exchangeToken.IsCancellationRequested.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(exchangeToken);
        control.ClientFaultStatusCode.ShouldBeNull();
        OperationCanceledException laterFailure = await Should.ThrowAsync<OperationCanceledException>(
            () => exchange.Request.Body.ReadAsync(new byte[1], CancellationToken.None).AsTask().WaitAsync(_timeout));
        laterFailure.CancellationToken.ShouldBe(exchangeToken);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Body: A failed DATA channel racing a peer abort should report exchange cancellation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_OnChannelFailureRacingPeerAbort_ShouldCancelWithExchangeToken(bool minimumRateEnabled)
    {
        // Arrange — force the channel-failure outcome of a wait racing its cancellation notification.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        Http2Context exchange = (Http2Context)await peer.ReceiveContextAsync();
        CompletionWinningReader reader = new();
        Http2RequestBodyDataRate? rate = minimumRateEnabled
            ? new(new HttpMinDataRate(100, TimeSpan.FromMinutes(1)), TimeProvider.System,
                () => false, () => 0, _ => ValueTask.CompletedTask)
            : null;
        using Http2RequestBodyStream body = new(exchange.Stream, reader, (_, _, _) => ValueTask.CompletedTask, rate);
        body.AttachExchange(exchange);
        Task<int> pending = body.ReadAsync(new byte[1], CancellationToken.None).AsTask();
        pending.IsCompleted.ShouldBeFalse();

        // Act — the stream abort is visible, but the pending wait reports its channel's IOException.
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
        await peer.SyncAsync();
        reader.Fail(new IOException("The connection closed before the DATA channel ended."));

        // Assert
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(_timeout));
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        failure.CancellationToken.ShouldBe(exchange.RequestCancelled);
        exchange.Stream.ClientFaultStatusCode.ShouldBeNull();
    }

    // A real channel can complete with its failure as cancellation races the pending wait. Keep that
    // outcome deterministic: the wait observes only channel completion, even after its token fires.
    private sealed class CompletionWinningReader : ChannelReader<Http2DataChunk>
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool TryRead(out Http2DataChunk item)
        {
            item = default;
            return false;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            return new ValueTask<bool>(_completion.Task);
        }

        public void Fail(Exception exception)
        {
            _completion.TrySetException(exception);
        }
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
