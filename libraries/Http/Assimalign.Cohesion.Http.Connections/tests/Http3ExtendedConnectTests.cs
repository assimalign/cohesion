using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The HTTP/3 extended CONNECT tunnel (RFC 9220, #1316): accepting answers <c>200</c> without ending
/// the request stream, the tunnel carries <c>DATA</c> both ways, either side can end it with its FIN,
/// the exchange's own finalization never sends a second head, and a reset or a lost connection faults
/// the tunnel's operations. Driven over the in-memory multiplexed driver (<see cref="Http3InMemoryPeer"/>),
/// which surfaces the server's reset reason to the client verbatim.
/// </summary>
public class Http3ExtendedConnectTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: AcceptAsync should send a 200 head with the application's headers and leave the stream open")]
    public async Task AcceptAsync_OnExtendedConnect_ShouldSend200HeadWithApplicationHeadersAndNoFin()
    {
        // Arrange — the application staged a status, a header the tunnel keeps, and fields it cannot carry.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        Connection request = await OpenExtendedConnectAsync(peer);
        IHttpContext context = await peer.NextContextAsync();
        context.Response.StatusCode = HttpStatusCode.Accepted;
        context.Response.Headers[new HttpHeaderKey("x-tunnel")] = "ready";
        context.Response.Headers[HttpHeaderKey.ContentLength] = "42";
        context.Response.Headers[HttpHeaderKey.Connection] = "Upgrade";
        context.Response.Headers[HttpHeaderKey.Upgrade] = "websocket";
        Http3FrameCollector output = new(request);

        // Act
        await using Stream tunnel = await context.ExtendedConnect!.AcceptAsync().AsTask().WaitAsync(_timeout);
        await output.ReadUntilAsync(collector => collector.Frames.Count >= 1, "the tunnel's response head");

        // Assert — RFC 9220: a 200 HEADERS frame and no FIN; RFC 9110 §9.3.6 / RFC 9114 §4.2: no
        // content-length and no connection-specific fields.
        output.Frames[0].FrameType.ShouldBe((long)Http3FrameType.Headers);
        Dictionary<string, string> headers = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(output.Frames[0].Payload);
        headers[":status"].ShouldBe("200");
        headers["x-tunnel"].ShouldBe("ready");
        headers.ShouldNotContainKey("content-length");
        headers.ShouldNotContainKey("connection");
        headers.ShouldNotContainKey("upgrade");
        output.IsCompleted.ShouldBeFalse();
        context.HasResponseStarted.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: The tunnel should carry an echo in both directions as DATA that leaves the stream open")]
    public async Task Tunnel_OnBidirectionalEcho_ShouldCarryDataBothWays()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        (Connection request, _, Stream tunnel, Http3FrameCollector output) = await AcceptTunnelAsync(peer);
        byte[] buffer = new byte[64];

        // Act — each message is read from the client's DATA and written straight back.
        foreach (string message in new[] { "hello", "tunnel", "over h3" })
        {
            byte[] payload = Encoding.ASCII.GetBytes(message);
            int echoed = output.DataLength;

            await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, payload));
            await tunnel.ReadExactlyAsync(buffer.AsMemory(0, payload.Length)).AsTask().WaitAsync(_timeout);
            await tunnel.WriteAsync(buffer.AsMemory(0, payload.Length)).AsTask().WaitAsync(_timeout);

            await output.ReadUntilAsync(collector => collector.DataLength >= echoed + payload.Length, $"the echo of '{message}'");
        }

        // Assert — the echo arrived in order, after one head, and the stream is still open.
        output.DataPayload().ShouldBe(Encoding.ASCII.GetBytes("hellotunnelover h3"));
        output.Frames.Count(frame => frame.FrameType == (long)Http3FrameType.Headers).ShouldBe(1);
        output.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: The client's FIN should end the reads while the server can still write and then end its side")]
    public async Task Tunnel_OnClientFin_ShouldEndReadsAndLeaveTheWriteSideOpen()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        (Connection request, IHttpContext context, Stream tunnel, Http3FrameCollector output) = await AcceptTunnelAsync(peer);

        // Act — the client sends its last octets and ends its side first.
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("bye")));
        request.Output.Complete();

        byte[] received = new byte[3];
        await tunnel.ReadExactlyAsync(received).AsTask().WaitAsync(_timeout);
        int end = await tunnel.ReadAsync(new byte[1]).AsTask().WaitAsync(_timeout);

        await tunnel.WriteAsync(Encoding.ASCII.GetBytes("ok")).AsTask().WaitAsync(_timeout);
        await tunnel.DisposeAsync().AsTask().WaitAsync(_timeout);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        await output.ReadUntilAsync(collector => collector.IsCompleted, "the server's FIN");

        // Assert — the read side ended at the client's FIN; the server still wrote, then ended its own
        // side with a FIN rather than a reset.
        Encoding.ASCII.GetString(received).ShouldBe("bye");
        end.ShouldBe(0);
        output.Failure.ShouldBeNull();
        output.Frames.Select(frame => frame.FrameType).ShouldBe(new[] { (long)Http3FrameType.Headers, (long)Http3FrameType.Data });
        output.DataPayload().ShouldBe(Encoding.ASCII.GetBytes("ok"));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: Disposing the tunnel should end the server's side with a FIN, and the exchange's end should stop reading with H3_NO_ERROR")]
    public async Task Tunnel_OnServerDispose_ShouldEndTheStreamAndStopReadingAtTheExchangeEnd()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        (Connection request, IHttpContext context, Stream tunnel, Http3FrameCollector output) = await AcceptTunnelAsync(peer);
        await tunnel.WriteAsync(Encoding.ASCII.GetBytes("last")).AsTask().WaitAsync(_timeout);

        // Act — the server ends its side first; the client never ends its own.
        await tunnel.DisposeAsync().AsTask().WaitAsync(_timeout);
        await output.ReadUntilAsync(collector => collector.IsCompleted, "the server's FIN");
        await Should.ThrowAsync<ObjectDisposedException>(() => tunnel.WriteAsync(new byte[1]).AsTask());

        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert — the tunnel's octets, one head, a FIN; then RFC 9114 §4.1: the client still sending is
        // told to stop with H3_NO_ERROR.
        output.Failure.ShouldBeNull();
        output.DataPayload().ShouldBe(Encoding.ASCII.GetBytes("last"));
        output.Frames.Count(frame => frame.FrameType == (long)Http3FrameType.Headers).ShouldBe(1);

        Http3StreamException stop = await Should.ThrowAsync<Http3StreamException>(() => request.Output.WriteAsync(new byte[1]).AsTask());
        stop.ErrorCode.ShouldBe(Http3ErrorCode.NoError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: The exchange's end should end a tunnel the application left open, with no second head")]
    public async Task SendAsync_OnTunnelLeftOpen_ShouldEndTheTunnelWithoutASecondHead()
    {
        // Arrange — the handler accepts, writes, and returns without disposing the tunnel.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        (_, IHttpContext context, Stream tunnel, Http3FrameCollector output) = await AcceptTunnelAsync(peer);
        await tunnel.WriteAsync(Encoding.ASCII.GetBytes("open")).AsTask().WaitAsync(_timeout);
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("a buffered body that must never be sent"));

        // Act
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        await output.ReadUntilAsync(collector => collector.IsCompleted, "the server's FIN");

        // Assert — the tunnel's own octets and nothing of the buffered response; the tunnel is closed.
        output.Frames.Select(frame => frame.FrameType).ShouldBe(new[] { (long)Http3FrameType.Headers, (long)Http3FrameType.Data });
        output.DataPayload().ShouldBe(Encoding.ASCII.GetBytes("open"));
        tunnel.CanWrite.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: A transfer of 1 MiB should cross the tunnel intact in both directions")]
    public async Task Tunnel_OnLargeTransfer_ShouldCarryItInBothDirections()
    {
        // Arrange — 1 MiB each way, well past a QUIC stream's initial flow-control window.
        const int length = 1024 * 1024;
        byte[] upload = Http2TestPeer.CreateBody(length);
        byte[] download = Enumerable.Reverse(upload).ToArray();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        (Connection request, _, Stream tunnel, Http3FrameCollector output) = await AcceptTunnelAsync(peer);

        // Act — the client sends in 64 KiB DATA frames while the server reads; then the server writes.
        Task<byte[]> reading = ReadExactlyAsync(tunnel, length);

        for (int offset = 0; offset < length; offset += 64 * 1024)
        {
            await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, upload.AsSpan(offset, 64 * 1024).ToArray()));
        }

        byte[] received = await reading.WaitAsync(_timeout);
        await tunnel.WriteAsync(download).AsTask().WaitAsync(_timeout);
        await output.ReadUntilAsync(collector => collector.DataLength >= length, "the server's 1 MiB");

        // Assert — compared as spans: an element-wise assertion over 1 MiB costs seconds.
        received.AsSpan().SequenceEqual(upload).ShouldBeTrue("the server should read the upload octet for octet");
        output.DataPayload().AsSpan().SequenceEqual(download).ShouldBeTrue("the client should read the download octet for octet");
        output.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: Accepting twice should throw InvalidOperationException")]
    public async Task AcceptAsync_OnSecondCall_ShouldThrowInvalidOperationException()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        (_, IHttpContext context, Stream tunnel, _) = await AcceptTunnelAsync(peer);

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(() => context.ExtendedConnect!.AcceptAsync().AsTask());
        tunnel.CanWrite.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: Accepting after the response started should throw InvalidOperationException")]
    public async Task AcceptAsync_AfterResponseStarted_ShouldThrowInvalidOperationException()
    {
        // Arrange — the streaming feature commits the response head with its first write.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            configureListener: static options =>
            {
                WithExtendedConnect(options);
                options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
            });
        await OpenExtendedConnectAsync(peer);
        IHttpContext context = await peer.NextContextAsync();
        await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("started")).AsTask().WaitAsync(_timeout);

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(() => context.ExtendedConnect!.AcceptAsync().AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: A peer reset should fault a pending read and later writes with IOException")]
    public async Task Tunnel_OnPeerReset_ShouldFaultPendingReadAndWrites()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        (Connection request, _, Stream tunnel, _) = await AcceptTunnelAsync(peer);
        Task<int> read = tunnel.ReadAsync(new byte[16]).AsTask();

        // Act — the client aborts the request stream in both directions.
        request.Abort();

        // Assert — a fault, never a clean end of the tunnel.
        await Should.ThrowAsync<IOException>(() => read.WaitAsync(_timeout));
        await Should.ThrowAsync<IOException>(() => tunnel.WriteAsync(new byte[16]).AsTask().WaitAsync(_timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: Losing the connection should fault a pending read and later writes with IOException")]
    public async Task Tunnel_OnConnectionLoss_ShouldFaultPendingReadAndWrites()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        (_, _, Stream tunnel, _) = await AcceptTunnelAsync(peer);
        await peer.StopReceivingAsync();
        Task<int> read = tunnel.ReadAsync(new byte[16]).AsTask();

        // Act — the QUIC connection goes away underneath the tunnel.
        await peer.Server.DisposeAsync();

        // Assert
        await Should.ThrowAsync<IOException>(() => read.WaitAsync(_timeout));
        await Should.ThrowAsync<IOException>(() => tunnel.WriteAsync(new byte[16]).AsTask().WaitAsync(_timeout));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 ExtendedConnect: Cancelling the exchange after accepting should reset the stream with H3_REQUEST_CANCELLED")]
    public async Task SendAsync_OnCancelledTunnel_ShouldResetWithRequestCancelled()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(configureListener: WithExtendedConnect);
        (_, IHttpContext context, Stream tunnel, Http3FrameCollector output) = await AcceptTunnelAsync(peer);

        // Act — the host abandons the exchange (a faulted handler, a stop budget run out).
        await context.CancelAsync();
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        await output.ReadUntilAsync(collector => collector.Failure is not null, "the reset of the request stream");

        // Assert — RFC 9114 §4.1.1: an abandoned exchange is reset with H3_REQUEST_CANCELLED, never ended
        // with a FIN.
        output.Failure.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.RequestCancelled);
        output.IsCompleted.ShouldBeFalse();
        await Should.ThrowAsync<ObjectDisposedException>(() => tunnel.WriteAsync(new byte[1]).AsTask());
    }

    // The extended CONNECT feature is installed by its interceptor (#1368), which a host registers.
    private static void WithExtendedConnect(HttpConnectionListenerOptions options)
        => options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor());

    private static async Task<Connection> OpenExtendedConnectAsync(Http3InMemoryPeer peer)
    {
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test")));
        return request;
    }

    private static async Task<(Connection Request, IHttpContext Context, Stream Tunnel, Http3FrameCollector Output)> AcceptTunnelAsync(Http3InMemoryPeer peer)
    {
        Connection request = await OpenExtendedConnectAsync(peer);
        IHttpContext context = await peer.NextContextAsync();
        Stream tunnel = await context.ExtendedConnect!.AcceptAsync().AsTask().WaitAsync(_timeout);
        Http3FrameCollector output = new(request);
        await output.ReadUntilAsync(collector => collector.Frames.Count >= 1, "the tunnel's response head");
        return (request, context, tunnel, output);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length)
    {
        byte[] buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }
}
