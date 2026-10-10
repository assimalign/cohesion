using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies that the buffered HTTP/2 response path — the default for every Web response — honors the
/// peer's flow-control windows (issue #1048, RFC 9113 §5.2 / §6.9): every DATA frame is covered by
/// connection- and stream-level credit before it is written, the writer waits for WINDOW_UPDATE when a
/// window is exhausted, a peer reset or a cancellation releases a waiting writer, and the windows are
/// debited exactly as the peer accounts for them, so its WINDOW_UPDATEs can never overflow them.
/// </summary>
public class Http2ResponseFlowControlTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // RFC 9113 §6.9.2 — the connection-level window starts at 65535 and SETTINGS never changes it.
    private const int connectionWindow = 65535;
    private const int maxFrameSize = 16384;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response FlowControl: A buffered body larger than the stream window should be paced by stream WINDOW_UPDATEs")]
    public async Task SendAsync_OnBodyLargerThanStreamWindow_ShouldPaceDataByWindowUpdates()
    {
        // Arrange — the peer advertises a 16 KiB stream window (the connection window is 65535).
        const int streamWindow = 16 * 1024;
        byte[] body = Http2TestPeer.CreateBody(40_000);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            initialWindowSize: streamWindow);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/large"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(body);

        // Act — only the first window's worth can go out.
        Task send = peer.ConnectionContext.SendAsync(exchange).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= streamWindow, "the first stream window of DATA");
        await peer.SyncAsync();

        // Assert — the writer stopped exactly at the window and is waiting for credit.
        peer.Output.DataLength(1).ShouldBe(streamWindow);
        send.IsCompleted.ShouldBeFalse();

        // Act — each WINDOW_UPDATE releases exactly the credited octets.
        await peer.SendWindowUpdateAsync(1, streamWindow);
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 2 * streamWindow, "the second stream window of DATA");
        await peer.SyncAsync();

        peer.Output.DataLength(1).ShouldBe(2 * streamWindow);
        send.IsCompleted.ShouldBeFalse();

        await peer.SendWindowUpdateAsync(1, streamWindow);
        await send.WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsData && frame.StreamId == 1 && frame.EndStream), "END_STREAM on the last DATA frame");

        // Assert — the body arrived intact, within MAX_FRAME_SIZE, END_STREAM on the last frame only.
        peer.Output.DataPayload(1).ShouldBe(body);
        Http2WireFrame[] dataFrames = peer.Output.ForStream(1).Where(frame => frame.IsData).ToArray();
        dataFrames.ShouldAllBe(frame => frame.Payload.Length <= maxFrameSize);
        dataFrames.Count(frame => frame.EndStream).ShouldBe(1);
        dataFrames[^1].EndStream.ShouldBeTrue();

        Http2WireFrame head = peer.Output.ForStream(1).First(frame => frame.IsHeaders);
        head.EndStream.ShouldBeFalse();
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload)["content-length"].ShouldBe("40000");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response FlowControl: A buffered body larger than the connection window should wait for a connection WINDOW_UPDATE")]
    public async Task SendAsync_OnBodyLargerThanConnectionWindow_ShouldWaitForConnectionWindowUpdate()
    {
        // Arrange — a generous 1 MiB stream window, so the fixed 65535-octet connection window is the
        // binding limit (RFC 9113 §5.2: a DATA frame consumes both windows).
        const int remainder = 34_465;
        byte[] body = Http2TestPeer.CreateBody(connectionWindow + remainder);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            initialWindowSize: 1024 * 1024);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/large"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(body);

        // Act
        Task send = peer.ConnectionContext.SendAsync(exchange).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= connectionWindow, "a full connection window of DATA");
        await peer.SyncAsync();

        // Assert — stopped at the connection window.
        peer.Output.DataLength(1).ShouldBe(connectionWindow);
        send.IsCompleted.ShouldBeFalse();

        // Act — connection-level credit alone resumes the stream.
        await peer.SendWindowUpdateAsync(0, remainder);
        await send.WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsData && frame.StreamId == 1 && frame.EndStream), "END_STREAM on the last DATA frame");

        // Assert
        peer.Output.DataPayload(1).ShouldBe(body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response FlowControl: Many responses should keep the send window consistent so replenishment never overflows it")]
    public async Task SendAsync_OnManyResponsesAtMaximumWindow_ShouldNotOverflowSendWindow()
    {
        // Arrange — the peer opens the connection window to its 2^31-1 maximum (RFC 9113 §6.9.1) and
        // then, like any compliant client, credits back every DATA octet it receives. A server that
        // did not debit its window for buffered DATA would be pushed past 2^31-1 by the first credit and
        // answer this compliant peer with GOAWAY(FLOW_CONTROL_ERROR); a correct one stays exactly at
        // the maximum across any number of responses.
        const int responses = 8;
        const int responseLength = 20_000;
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendWindowUpdateAsync(0, int.MaxValue - connectionWindow);

        // Act
        for (int index = 0; index < responses; index++)
        {
            int streamId = 1 + (2 * index);
            await peer.SendHeadersAsync(streamId, endStream: true, Http2TestPeer.Get($"/item/{index}"));
            IHttpContext exchange = await peer.ReceiveContextAsync();
            exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(responseLength));
            await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);

            await peer.Output.ReadUntilAsync(
                frames => frames.Any(frame => frame.IsData && frame.StreamId == streamId && frame.EndStream),
                $"the end of response {index}");
            peer.Output.DataLength(streamId).ShouldBe(responseLength);

            // The compliant peer credits the connection for everything it received.
            await peer.SendWindowUpdateAsync(0, responseLength);
        }

        await peer.SyncAsync();

        // Assert — no connection error was ever raised against the compliant peer.
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response FlowControl: A peer reset should release a buffered writer waiting for credit")]
    public async Task SendAsync_OnPeerResetWhileAwaitingCredit_ShouldAbandonResponse()
    {
        // Arrange — a 10-octet stream window parks the writer after its first DATA frame.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            initialWindowSize: 10);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/slow"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(100));

        Task send = peer.ConnectionContext.SendAsync(exchange).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 10, "the first window of DATA");
        await peer.SyncAsync();
        send.IsCompleted.ShouldBeFalse();

        // Act — RFC 9113 §5.4.2: after the peer's RST_STREAM no further frame may be sent on the stream.
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);

        // Assert — the writer returns (the exchange is simply over) and never writes again.
        await send.WaitAsync(_timeout);
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        await peer.SyncAsync();
        peer.Output.DataLength(1).ShouldBe(10);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.EndStream);

        // The connection keeps serving: the next stream is answered normally.
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();
        next.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("ok"));
        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsData && frame.StreamId == 3 && frame.EndStream), "the response on stream 3");
        Encoding.ASCII.GetString(peer.Output.DataPayload(3)).ShouldBe("ok");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response FlowControl: Cancellation should release a buffered writer waiting for credit and reset its stream")]
    public async Task SendAsync_OnCancellationWhileAwaitingCredit_ShouldResetStreamWithCancel()
    {
        // Arrange — one concurrent stream, and a 10-octet stream window that parks the writer after its
        // first DATA frame.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            configure: static http2 => http2.Limits.MaxStreamsPerConnection = 1,
            initialWindowSize: 10);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/slow"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(Http2TestPeer.CreateBody(100));

        using CancellationTokenSource cancellation = new();
        Task send = peer.ConnectionContext.SendAsync(exchange, cancellation.Token).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 10, "the first window of DATA");
        await peer.SyncAsync();
        send.IsCompleted.ShouldBeFalse();

        // Act
        cancellation.Cancel();

        // Assert — the wait for WINDOW_UPDATE honors the token, and the response the stream can no
        // longer complete is ended with RST_STREAM(CANCEL) (#1075).
        await Should.ThrowAsync<OperationCanceledException>(() => send.WaitAsync(_timeout));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        peer.Output.DataLength(1).ShouldBe(10);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.EndStream);

        // The exchange ended with its send, so the stream's slot is free for the next one.
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();
        next.Request.Path.Value.ShouldBe("/next");
        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Response FlowControl: A peer reset should release a streaming writer waiting for credit")]
    public async Task StreamingWrite_OnPeerResetWhileAwaitingCredit_ShouldReturn()
    {
        // Arrange — the streaming sink shares the same credit mechanism as the buffered path.
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            options,
            initialWindowSize: 4);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/events"));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        Task write = exchange.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("0123456789")).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 4, "the first window of streamed DATA");
        await peer.SyncAsync();
        write.IsCompleted.ShouldBeFalse();

        // Act
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);

        // Assert — the parked write is released; the rest of the body is discarded, not sent.
        await write.WaitAsync(_timeout);
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();
        peer.Output.DataLength(1).ShouldBe(4);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.EndStream);
    }
}
