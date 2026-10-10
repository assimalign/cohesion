using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// An HTTP/2 send cancelled once its response is claimed (#1075). The stream can carry no other
/// response, so the transport resets it with <c>RST_STREAM(CANCEL)</c> (RFC 9113 §8.1, §6.4): the peer
/// learns the response will not complete, the stream's slot is free once the exchange ends, and the
/// connection's graceful close does not wait on it. The reset is written even when the caller's token
/// was already cancelled. A send cancelled once its <c>END_STREAM</c> reached the transport ends the
/// stream like a completed response instead, and an extended CONNECT tunnel whose end is cancelled is
/// not left open.
/// </summary>
public class Http2CancelledSendTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private const byte windowUpdateType = 0x8;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Cancelled Sends: Closing the connection should not wait for a stream whose send was cancelled")]
    public async Task GracefulCloseAsync_AfterSendCancelledMidResponse_ShouldNotWaitForStream()
    {
        // Arrange — a 10-octet stream window parks the buffered writer after its first DATA frame, and
        // the send is then cancelled.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(initialWindowSize: 10);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/slow"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(100));

        using CancellationTokenSource cancellation = new();
        Task send = peer.ConnectionContext.SendAsync(exchange, cancellation.Token).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 10, "the first window of DATA");
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => send.WaitAsync(_timeout));

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        await ((Http2ConnectionContext)peer.ConnectionContext).GracefulCloseAsync().AsTask().WaitAsync(_timeout);
        elapsed.Stop();

        // Assert — the reset ended the exchange for the drain, which would otherwise wait out its
        // five-second window for a response that can never complete.
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Cancelled Sends: A streamed response whose completion is cancelled should be reset with CANCEL")]
    public async Task SendAsync_OnCancelledCompletionOfStreamedResponse_ShouldResetStreamWithCancel()
    {
        // Arrange — the application streamed part of its response through the raw sink.
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/events"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("partial")).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 7, "the streamed DATA");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // Act — the send that would end the streamed response is cancelled before it writes END_STREAM.
        await Should.ThrowAsync<OperationCanceledException>(
            () => peer.ConnectionContext.SendAsync(exchange, cancellation.Token).AsTask().WaitAsync(_timeout));

        // Assert — the response can never end, so the stream is reset rather than left open.
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.EndStream);
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Cancelled Sends: A reset requested with an already cancelled token should still be written")]
    public async Task SendAsync_OnCancelledExchangeWithCancelledToken_ShouldStillWriteReset()
    {
        // Arrange — a host gave up on the exchange: its stop budget ran out, so the token it resets the
        // exchange with is already cancelled.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/abandoned"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // Act
        exchange.Cancel();
        await peer.ConnectionContext.SendAsync(exchange, cancellation.Token).AsTask().WaitAsync(_timeout);

        // Assert — the reset still ends the stream for the peer.
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Cancelled Sends: A send cancelled once its END_STREAM reached the transport should end the stream without a reset")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_OnCancellationAfterEndStreamHandedOver_ShouldEndStreamWithoutReset(bool inFlush)
    {
        // Arrange — a POST whose 100-octet body the handler never reads, so its stream owes the
        // connection window 100 octets. The tap cancels the send once the response's END_STREAM frame
        // reached the transport: inside that frame's write, or in the flush after it.
        using CancellationTokenSource cancellation = new();
        Http2OutputTapConnection? tap = null;
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(decorate: transport => tap = new Http2OutputTapConnection(transport));
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await peer.SendDataAsync(1, Http2TestPeer.CreateBody(100), endStream: true);
        await peer.SyncAsync();
        long creditBefore = WindowCredit(peer.Output.Frames, streamId: 0);
        exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("stored"));

        bool endStreamOut = false;
        tap!.OnFrameStarted = frame =>
        {
            if (IsEndOfStream(frame, streamId: 1))
            {
                endStreamOut = true;

                if (!inFlush)
                {
                    cancellation.Cancel();
                }
            }
        };
        tap.OnFlush = () =>
        {
            if (inFlush && endStreamOut)
            {
                cancellation.Cancel();
            }
        };

        // Act
        await Should.ThrowAsync<OperationCanceledException>(
            () => peer.ConnectionContext.SendAsync(exchange, cancellation.Token).AsTask().WaitAsync(_timeout));
        await peer.SyncAsync();

        // Assert — RFC 9113 §5.1: both sides ended the stream, so no RST_STREAM may follow the
        // response. The stream is removed, and the connection WINDOW_UPDATE still returns its debt.
        IReadOnlyList<Http2WireFrame> stream = peer.Output.ForStream(1);
        stream.ShouldContain(frame => frame.IsData && frame.EndStream);
        stream.ShouldNotContain(frame => frame.IsRstStream);
        (WindowCredit(peer.Output.Frames, streamId: 0) - creditBefore).ShouldBe(100);
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Cancelled Sends: A send cancelled while an extended CONNECT tunnel's end is written should not leave the stream open")]
    public async Task SendAsync_OnCancelledTunnelEnd_ShouldNotLeaveStreamOpen()
    {
        // Arrange — an accepted tunnel whose client is still sending. The transport takes the
        // END_STREAM that ends the tunnel's side and then makes the writer wait, which holds the
        // connection's write gate.
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Http2OutputTapConnection? tap = null;
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options, decorate: transport => tap = new Http2OutputTapConnection(transport));
        await peer.SendHeadersAsync(1, endStream: false, ExtendedConnect());
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await using Stream tunnel = await exchange.ExtendedConnect!.AcceptAsync().AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsHeaders && frame.StreamId == 1), "the tunnel's response head");
        tap!.HoldAfterFrame = frame => IsEndOfStream(frame, streamId: 1) ? HoldAsync() : Task.CompletedTask;

        async Task HoldAsync()
        {
            held.TrySetResult();
            await release.Task;
        }

        using CancellationTokenSource cancellation = new();
        Task send = peer.ConnectionContext.SendAsync(exchange, cancellation.Token).AsTask();
        await held.Task.WaitAsync(_timeout);

        // Act — the host gives up while the tunnel's end waits on the transport, which then drains.
        cancellation.Cancel();
        release.TrySetResult();
        await Should.ThrowAsync<OperationCanceledException>(() => send.WaitAsync(_timeout));
        await peer.SyncAsync();

        // Assert — the stream is not left open: its END_STREAM is out, so it ends as after any complete
        // response, with RST_STREAM(NO_ERROR) to stop the client still sending (RFC 9113 §8.1), and no
        // CANCEL follows the END_STREAM.
        IReadOnlyList<Http2WireFrame> stream = peer.Output.ForStream(1);
        stream.ShouldContain(frame => frame.IsData && frame.EndStream);
        stream.Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.NoError);
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    private static (string Name, string Value)[] ExtendedConnect() =>
    [
        (":method", "CONNECT"),
        (":protocol", "websocket"),
        (":scheme", "https"),
        (":path", "/chat"),
        (":authority", "api.test"),
    ];

    // Whether the frame is a DATA frame on the stream that carries END_STREAM.
    private static bool IsEndOfStream(Http2OutputTapConnection.FrameHeader frame, int streamId)
        => frame.Type == Http2WireFrame.DataType && frame.StreamId == streamId && (frame.Flags & 0x1) != 0;

    // The total credit the server granted on a stream (0 = the connection) through WINDOW_UPDATE.
    private static long WindowCredit(IReadOnlyList<Http2WireFrame> frames, int streamId)
        => frames
            .Where(frame => frame.Type == windowUpdateType && frame.StreamId == streamId)
            .Sum(frame => (long)(BinaryPrimitives.ReadUInt32BigEndian(frame.Payload) & 0x7FFFFFFF));
}
