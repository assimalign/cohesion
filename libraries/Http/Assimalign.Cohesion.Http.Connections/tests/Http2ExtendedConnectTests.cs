using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The HTTP/2 extended CONNECT tunnel (RFC 8441 §5, #1316): accepting answers <c>200</c> without
/// ending the stream, the tunnel carries <c>DATA</c> both ways under flow control, either side can end
/// it, the exchange's own finalization never sends a second head, and a reset or a lost connection
/// faults the tunnel's pending operations. Driven by an interactive peer over the in-memory driver
/// (<see cref="Http2TestPeer"/>).
/// </summary>
public class Http2ExtendedConnectTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // RFC 9113 §6.9.2 — the server advertises no SETTINGS_INITIAL_WINDOW_SIZE, so its stream receive
    // windows, like every connection window, start at 65535.
    private const int receiveWindow = 65535;
    private const int maxFrameSize = 16384;
    private const byte windowUpdateType = 0x8;

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: AcceptAsync should send a 200 head with the application's headers and no END_STREAM")]
    public async Task AcceptAsync_OnExtendedConnect_ShouldSend200HeadWithApplicationHeadersAndNoEndStream()
    {
        // Arrange — the application staged a status, a header the tunnel keeps, and fields it cannot carry.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        await peer.SendHeadersAsync(1, endStream: false, ExtendedConnect());
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.StatusCode = HttpStatusCode.Accepted;
        context.Response.Headers[new HttpHeaderKey("x-tunnel")] = "ready";
        context.Response.Headers[HttpHeaderKey.ContentLength] = "42";
        context.Response.Headers[HttpHeaderKey.Connection] = "Upgrade";
        context.Response.Headers[HttpHeaderKey.Upgrade] = "websocket";

        // Act
        await using Stream tunnel = await context.ExtendedConnect!.AcceptAsync().AsTask().WaitAsync(_timeout);
        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(
            output => output.Any(frame => frame.IsHeaders && frame.StreamId == 1),
            "the tunnel's response head");

        // Assert — RFC 8441 §5: a 200 that leaves the stream open; RFC 9110 §9.3.6 / RFC 9113 §8.2.2: no
        // content-length and no connection-specific fields.
        Http2WireFrame head = frames.Single(frame => frame.IsHeaders && frame.StreamId == 1);
        head.EndStream.ShouldBeFalse();
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload);
        headers[":status"].ShouldBe("200");
        headers["x-tunnel"].ShouldBe("ready");
        headers.ShouldNotContainKey("content-length");
        headers.ShouldNotContainKey("connection");
        headers.ShouldNotContainKey("upgrade");
        context.HasResponseStarted.ShouldBeTrue();
        tunnel.CanRead.ShouldBeTrue();
        tunnel.CanWrite.ShouldBeTrue();

        await EndExchangeAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: The tunnel should carry an echo in both directions as DATA that leaves the stream open")]
    public async Task Tunnel_OnBidirectionalEcho_ShouldCarryDataBothWays()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        (IHttpContext context, Stream tunnel) = await AcceptTunnelAsync(peer);
        byte[] buffer = new byte[64];

        // Act — each message is read from the client's DATA and written straight back.
        foreach (string message in new[] { "hello", "tunnel", "over h2" })
        {
            byte[] payload = Encoding.ASCII.GetBytes(message);
            int echoed = peer.Output.DataLength(1);

            await peer.SendDataAsync(1, payload, endStream: false);
            await tunnel.ReadExactlyAsync(buffer.AsMemory(0, payload.Length)).AsTask().WaitAsync(_timeout);
            await tunnel.WriteAsync(buffer.AsMemory(0, payload.Length)).AsTask().WaitAsync(_timeout);

            await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= echoed + payload.Length, $"the echo of '{message}'");
        }

        // Assert — the echo arrived in order, as DATA that never ended the stream, after one head.
        peer.Output.DataPayload(1).ShouldBe(Encoding.ASCII.GetBytes("hellotunnelover h2"));
        IReadOnlyList<Http2WireFrame> stream = peer.Output.ForStream(1);
        stream.Count(frame => frame.IsHeaders).ShouldBe(1);
        stream.ShouldNotContain(frame => frame.EndStream || frame.IsRstStream);
        context.RequestCancelled.IsCancellationRequested.ShouldBeFalse();

        await EndExchangeAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: The client's END_STREAM should end the reads while the server can still write and then end its side")]
    public async Task Tunnel_OnClientEndStream_ShouldEndReadsAndLeaveTheWriteSideOpen()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        (IHttpContext context, Stream tunnel) = await AcceptTunnelAsync(peer);

        // Act — the client sends its last octets and ends its side first.
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("bye"), endStream: true);
        byte[] received = new byte[3];
        await tunnel.ReadExactlyAsync(received).AsTask().WaitAsync(_timeout);
        int end = await tunnel.ReadAsync(new byte[1]).AsTask().WaitAsync(_timeout);

        await tunnel.WriteAsync(Encoding.ASCII.GetBytes("ok")).AsTask().WaitAsync(_timeout);
        await tunnel.DisposeAsync().AsTask().WaitAsync(_timeout);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — the read side ended at the client's END_STREAM; the server still wrote, then ended its
        // own side with END_STREAM. Both sides ended, so the stream is removed without a reset.
        Encoding.ASCII.GetString(received).ShouldBe("bye");
        end.ShouldBe(0);
        peer.Output.DataPayload(1).ShouldBe(Encoding.ASCII.GetBytes("ok"));
        IReadOnlyList<Http2WireFrame> stream = peer.Output.ForStream(1);
        stream[^1].IsData.ShouldBeTrue();
        stream[^1].EndStream.ShouldBeTrue();
        stream.Count(frame => frame.IsHeaders).ShouldBe(1);
        stream.ShouldNotContain(frame => frame.IsRstStream);
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: Disposing the tunnel should end the server's side, and the exchange's end should stop a client still sending with NO_ERROR")]
    public async Task Tunnel_OnServerDispose_ShouldEndStreamAndResetWithNoErrorAtTheExchangeEnd()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        (IHttpContext context, Stream tunnel) = await AcceptTunnelAsync(peer);
        await tunnel.WriteAsync(Encoding.ASCII.GetBytes("last")).AsTask().WaitAsync(_timeout);

        // Act — the server ends its side first; the client never ends its own.
        await tunnel.DisposeAsync().AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsData && frame.StreamId == 1 && frame.EndStream), "END_STREAM on the tunnel");
        ObjectDisposedException disposed = await Should.ThrowAsync<ObjectDisposedException>(() => tunnel.WriteAsync(new byte[1]).AsTask());

        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(
            output => output.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the RST_STREAM that ends the exchange");

        // Assert — RFC 9113 §8.1: the client still sending is told to stop with NO_ERROR; the exchange's
        // finalization sent no second head and no further DATA.
        disposed.ShouldNotBeNull();
        peer.Output.DataPayload(1).ShouldBe(Encoding.ASCII.GetBytes("last"));
        IReadOnlyList<Http2WireFrame> stream = peer.Output.ForStream(1);
        stream.Count(frame => frame.IsHeaders).ShouldBe(1);
        stream.Count(frame => frame.EndStream).ShouldBe(1);
        frames.Single(frame => frame.IsRstStream && frame.StreamId == 1).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.NoError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: The exchange's end should end a tunnel the application left open, with no second head")]
    public async Task SendAsync_OnTunnelLeftOpen_ShouldEndTheTunnelWithoutASecondHead()
    {
        // Arrange — the handler accepts, writes, and returns without disposing the tunnel.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        (IHttpContext context, Stream tunnel) = await AcceptTunnelAsync(peer);
        await tunnel.WriteAsync(Encoding.ASCII.GetBytes("open")).AsTask().WaitAsync(_timeout);
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("a buffered body that must never be sent"));

        // Act
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1), "the end of the exchange");

        // Assert — the tunnel was ended with END_STREAM after its own DATA; the buffered response was not
        // written, and the tunnel itself is closed.
        IReadOnlyList<Http2WireFrame> stream = peer.Output.ForStream(1);
        stream.Count(frame => frame.IsHeaders).ShouldBe(1);
        peer.Output.DataPayload(1).ShouldBe(Encoding.ASCII.GetBytes("open"));
        stream.Single(frame => frame.EndStream).IsData.ShouldBeTrue();
        tunnel.CanWrite.ShouldBeFalse();
        await Should.ThrowAsync<ObjectDisposedException>(() => tunnel.ReadAsync(new byte[1]).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: A write larger than the peer's window should be paced by its WINDOW_UPDATEs")]
    public async Task Tunnel_OnWriteLargerThanSendWindow_ShouldBePacedByWindowUpdates()
    {
        // Arrange — the peer advertises a 16 KiB stream window.
        const int streamWindow = 16 * 1024;
        byte[] payload = Http2TestPeer.CreateBody(100_000);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect(), initialWindowSize: streamWindow);
        (IHttpContext context, Stream tunnel) = await AcceptTunnelAsync(peer);

        // Act — only the first window can go out before the peer grants more.
        Task write = tunnel.WriteAsync(payload).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= streamWindow, "the first window of DATA");
        await peer.SyncAsync();

        peer.Output.DataLength(1).ShouldBe(streamWindow);
        write.IsCompleted.ShouldBeFalse();

        // The peer credits the stream and the connection as it consumes.
        while (peer.Output.DataLength(1) < payload.Length)
        {
            int received = peer.Output.DataLength(1);
            await peer.SendWindowUpdateAsync(1, streamWindow);
            await peer.SendWindowUpdateAsync(0, streamWindow);
            await peer.Output.ReadUntilAsync(
                _ => peer.Output.DataLength(1) >= Math.Min(payload.Length, received + streamWindow),
                "the next window of DATA");
        }

        await write.WaitAsync(_timeout);

        // Assert — the whole write arrived intact, within MAX_FRAME_SIZE, and the tunnel is still open.
        peer.Output.DataPayload(1).ShouldBe(payload);
        IReadOnlyList<Http2WireFrame> dataFrames = peer.Output.ForStream(1).Where(frame => frame.IsData).ToList();
        dataFrames.ShouldAllBe(frame => frame.Payload.Length <= maxFrameSize && !frame.EndStream);

        await EndExchangeAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: The client's transfer larger than the receive window should flow as the server reads and credits it")]
    public async Task Tunnel_OnClientTransferLargerThanReceiveWindow_ShouldFlowAsTheServerReads()
    {
        // Arrange — 200 KB is three times the server's stream and connection receive windows.
        byte[] payload = Http2TestPeer.CreateBody(200_000);
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        (IHttpContext context, Stream tunnel) = await AcceptTunnelAsync(peer);
        Task<byte[]> reading = ReadExactlyAsync(tunnel, payload.Length);

        // Act — a compliant client never sends beyond the credit the server granted: the initial windows
        // plus every WINDOW_UPDATE the server emits as its reader consumes.
        int sent = 0;

        while (sent < payload.Length)
        {
            IReadOnlyList<Http2WireFrame> frames = peer.Output.Frames;
            long credit = Math.Min(receiveWindow + WindowCredit(frames, 1), receiveWindow + WindowCredit(frames, 0)) - sent;

            if (credit <= 0)
            {
                int current = sent;
                await peer.Output.ReadUntilAsync(
                    output => Math.Min(receiveWindow + WindowCredit(output, 1), receiveWindow + WindowCredit(output, 0)) > current,
                    "the server's WINDOW_UPDATE for more of the tunnel's DATA");
                continue;
            }

            int chunk = (int)Math.Min(Math.Min(maxFrameSize, credit), payload.Length - sent);
            await peer.SendDataAsync(1, payload.AsSpan(sent, chunk).ToArray(), endStream: false);
            sent += chunk;
        }

        byte[] received = await reading.WaitAsync(_timeout);

        // Assert — everything arrived, which required the server to credit the windows as it read.
        received.ShouldBe(payload);
        WindowCredit(peer.Output.Frames, 1).ShouldBeGreaterThan(0);
        WindowCredit(peer.Output.Frames, 0).ShouldBeGreaterThan(0);

        await EndExchangeAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: Accepting twice should throw InvalidOperationException")]
    public async Task AcceptAsync_OnSecondCall_ShouldThrowInvalidOperationException()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        (IHttpContext context, Stream tunnel) = await AcceptTunnelAsync(peer);

        // Act / Assert — and the second call wrote nothing.
        await Should.ThrowAsync<InvalidOperationException>(() => context.ExtendedConnect!.AcceptAsync().AsTask());
        await peer.SyncAsync();
        peer.Output.ForStream(1).Count(frame => frame.IsHeaders).ShouldBe(1);
        tunnel.CanWrite.ShouldBeTrue();

        await EndExchangeAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: Accepting after the response started should throw InvalidOperationException")]
    public async Task AcceptAsync_AfterResponseStarted_ShouldThrowInvalidOperationException()
    {
        // Arrange — the streaming feature commits the response head with its first write.
        HttpConnectionListenerOptions options = WithExtendedConnect();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: false, ExtendedConnect());
        IHttpContext context = await peer.ReceiveContextAsync();
        await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("started")).AsTask().WaitAsync(_timeout);

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(() => context.ExtendedConnect!.AcceptAsync().AsTask());

        await EndExchangeAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: Accepting a cancelled exchange should throw InvalidOperationException")]
    public async Task AcceptAsync_OnCancelledExchange_ShouldThrowInvalidOperationException()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        await peer.SendHeadersAsync(1, endStream: false, ExtendedConnect());
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Cancel();

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(() => context.ExtendedConnect!.AcceptAsync().AsTask());

        await EndExchangeAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: Accepting a stream the peer already reset should throw IOException")]
    public async Task AcceptAsync_OnStreamResetByPeer_ShouldThrowIOException()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        await peer.SendHeadersAsync(1, endStream: false, ExtendedConnect());
        IHttpContext context = await peer.ReceiveContextAsync();
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
        await peer.SyncAsync();

        // Act / Assert
        await Should.ThrowAsync<IOException>(() => context.ExtendedConnect!.AcceptAsync().AsTask());
        await peer.SyncAsync();
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsHeaders);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: A peer reset should fault a pending read with IOException")]
    public async Task Tunnel_OnPeerReset_ShouldFaultPendingRead()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        (_, Stream tunnel) = await AcceptTunnelAsync(peer);
        Task<int> read = tunnel.ReadAsync(new byte[16]).AsTask();

        // Act
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);

        // Assert — a fault, never a clean end of the tunnel.
        await Should.ThrowAsync<IOException>(() => read.WaitAsync(_timeout));
        await Should.ThrowAsync<IOException>(() => tunnel.ReadAsync(new byte[16]).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: A peer reset should fault a write waiting for flow-control credit with IOException")]
    public async Task Tunnel_OnPeerReset_ShouldFaultPendingWrite()
    {
        // Arrange — a 1 KiB stream window parks a 4 KiB write after its first frame.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect(), initialWindowSize: 1024);
        (_, Stream tunnel) = await AcceptTunnelAsync(peer);
        Task write = tunnel.WriteAsync(new byte[4096]).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 1024, "the first window of DATA");
        write.IsCompleted.ShouldBeFalse();

        // Act
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);

        // Assert
        await Should.ThrowAsync<IOException>(() => write.WaitAsync(_timeout));
        await Should.ThrowAsync<IOException>(() => tunnel.WriteAsync(new byte[1]).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: Losing the connection should fault a pending read and a pending write with IOException")]
    public async Task Tunnel_OnConnectionLoss_ShouldFaultPendingReadAndWrite()
    {
        // Arrange — a read waits for DATA and a write waits for credit.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect(), initialWindowSize: 1024);
        (_, Stream tunnel) = await AcceptTunnelAsync(peer);
        Task<int> read = tunnel.ReadAsync(new byte[16]).AsTask();
        Task write = tunnel.WriteAsync(new byte[4096]).AsTask();
        await peer.Output.ReadUntilAsync(_ => peer.Output.DataLength(1) >= 1024, "the first window of DATA");

        // Act — the client's side of the connection ends: no frame, no credit, can arrive any more.
        peer.Transport.CompleteInput();

        // Assert
        await Should.ThrowAsync<IOException>(() => read.WaitAsync(_timeout));
        await Should.ThrowAsync<IOException>(() => write.WaitAsync(_timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: Cancelling the exchange after accepting should reset the stream with CANCEL")]
    public async Task SendAsync_OnCancelledTunnel_ShouldResetWithCancel()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(WithExtendedConnect());
        (IHttpContext context, Stream tunnel) = await AcceptTunnelAsync(peer);

        // Act — the host abandons the exchange (a faulted handler, a stop budget run out).
        await context.CancelAsync();
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(
            output => output.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the RST_STREAM");

        // Assert — RFC 8441 §5: an abortive close is RST_STREAM(CANCEL), never an orderly END_STREAM.
        frames.Single(frame => frame.IsRstStream && frame.StreamId == 1).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.Cancel);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.EndStream);
        await Should.ThrowAsync<ObjectDisposedException>(() => tunnel.WriteAsync(new byte[1]).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 ExtendedConnect: The streaming response sink should refuse to start after a tunnel was accepted")]
    public async Task StreamingSink_AfterTunnelAccepted_ShouldRefuseToCommitASecondHead()
    {
        // Arrange
        HttpConnectionListenerOptions options = WithExtendedConnect();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        (IHttpContext context, _) = await AcceptTunnelAsync(peer);

        // Act / Assert — the tunnel took the exchange over; no second HEADERS reaches the wire.
        await Should.ThrowAsync<InvalidOperationException>(() => context.Response.Streaming.WriteAsync(new byte[1]).AsTask());
        await peer.SyncAsync();
        peer.Output.ForStream(1).Count(frame => frame.IsHeaders).ShouldBe(1);

        await EndExchangeAsync(peer, context);
    }

    // The extended CONNECT feature is installed by its interceptor (#1368), which a host registers.
    private static HttpConnectionListenerOptions WithExtendedConnect()
    {
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor());
        return options;
    }

    private static (string Name, string Value)[] ExtendedConnect() =>
    [
        (":method", "CONNECT"),
        (":protocol", "websocket"),
        (":scheme", "https"),
        (":path", "/chat"),
        (":authority", "api.test"),
    ];

    private static async Task<(IHttpContext Context, Stream Tunnel)> AcceptTunnelAsync(Http2TestPeer peer)
    {
        await peer.SendHeadersAsync(1, endStream: false, ExtendedConnect());
        IHttpContext context = await peer.ReceiveContextAsync();
        Stream tunnel = await context.ExtendedConnect!.AcceptAsync().AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsHeaders && frame.StreamId == 1), "the tunnel's response head");
        return (context, tunnel);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length)
    {
        byte[] buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    // Finalizes the exchange as a host does once its handler returns, so the connection's graceful close
    // at disposal has no in-flight exchange to wait for.
    private static Task EndExchangeAsync(Http2TestPeer peer, IHttpContext context)
        => peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

    // The total credit the server granted on a stream (0 = the connection) through WINDOW_UPDATE.
    private static long WindowCredit(IReadOnlyList<Http2WireFrame> frames, int streamId)
        => frames
            .Where(frame => frame.Type == windowUpdateType && frame.StreamId == streamId)
            .Sum(frame => (long)(BinaryPrimitives.ReadUInt32BigEndian(frame.Payload) & 0x7FFFFFFF));
}
