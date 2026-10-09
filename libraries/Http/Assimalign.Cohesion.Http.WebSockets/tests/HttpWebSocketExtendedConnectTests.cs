using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.WebSockets.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.WebSockets.Tests;

/// <summary>
/// <c>context.WebSockets</c> over HTTP/2 and HTTP/3 (RFC 8441 §5, RFC 9220 §3): the opening handshake
/// is an extended CONNECT whose <c>:protocol</c> is <c>websocket</c>, carried here by a double of the
/// <see cref="IHttpExtendedConnectFeature"/> the Http.ExtendedConnect interceptor installs. Validation, the refusals, and the accept.
/// </summary>
public class HttpWebSocketExtendedConnectTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: A websocket extended CONNECT with version 13 is a valid handshake, installed once")]
    [InlineData(HttpVersion.Http20)]
    [InlineData(HttpVersion.Http30)]
    public void WebSockets_WebSocketExtendedConnect_IsValidAndInstalledOnce(HttpVersion version)
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect(version: version);

        // Act
        IHttpWebSocketFeature first = context.WebSockets;
        IHttpWebSocketFeature second = context.WebSockets;

        // Assert
        first.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.Valid);
        first.IsWebSocketRequest.ShouldBeTrue();
        second.ShouldBeSameAs(first);
        context.Features.Get<IHttpWebSocketFeature>().ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: The :protocol token is matched case-insensitively")]
    public void WebSockets_MixedCaseProtocol_IsAHandshake()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect(protocol: "WebSocket");

        // Act / Assert
        context.WebSockets.IsWebSocketRequest.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: An extended CONNECT for another protocol is not a WebSocket request")]
    public void WebSockets_ExtendedConnectForAnotherProtocol_ReturnsNone()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect(protocol: "connect-udp");

        // Act
        IHttpWebSocketFeature webSockets = context.WebSockets;

        // Assert
        webSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.None);
        context.Features.Get<IHttpWebSocketFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: A classic CONNECT, which the transport surfaces without the feature, is not a WebSocket request")]
    public void WebSockets_ClassicConnect_ReturnsNone()
    {
        // Arrange — RFC 9110 §9.3.6: a CONNECT without :protocol asks for a TCP tunnel, not a WebSocket.
        WebSocketTestContext context = new(HttpMethod.Connect, HttpVersion.Http20);
        context.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "13";

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.None);
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: A version other than 13, or none, is unsupported")]
    [InlineData(null)]
    [InlineData("8")]
    public void WebSockets_OtherVersion_IsUnsupportedVersion(string? version)
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect();
        SetOrRemove(context.Request.Headers, HttpHeaderKey.SecWebSocketVersion, version);

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.UnsupportedVersion);
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: The handshake needs no key, and a key the client sends is ignored")]
    [InlineData(null)]
    [InlineData("not-a-key")]
    public void WebSockets_AnyKey_IsIgnored(string? key)
    {
        // Arrange — RFC 8441 §5: the :protocol pseudo-header supersedes Sec-WebSocket-Key.
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect();
        SetOrRemove(context.Request.Headers, HttpHeaderKey.SecWebSocketKey, key);

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.Valid);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: A malformed subprotocol list is invalid and refused with 400")]
    public void RejectHandshake_MalformedSubprotocolList_Stages400()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect();
        context.Request.Headers[HttpHeaderKey.SecWebSocketProtocol] = "chat room";

        // Act
        IHttpWebSocketFeature webSockets = context.WebSockets;
        webSockets.RejectHandshake();

        // Assert
        webSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.Invalid);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        context.ExtendedConnect!.AcceptCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: An unsupported version is refused with 426 and the version, without Upgrade or Connection")]
    public void RejectHandshake_UnsupportedVersion_Stages426WithoutConnectionSpecificFields()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect();
        context.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "8";

        // Act
        context.WebSockets.RejectHandshake();

        // Assert — RFC 6455 §4.4's version; RFC 9113 §8.2.2 / RFC 9114 §4.2 prohibit the
        // connection-specific Upgrade and Connection fields an HTTP/1.1 426 carries.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.UpgradeRequired);
        context.ResponseHeader(HttpHeaderKey.SecWebSocketVersion).ShouldBe("13");
        context.ResponseHeader(HttpHeaderKey.Upgrade).ShouldBeNull();
        context.ResponseHeader(HttpHeaderKey.Connection).ShouldBeNull();
        context.ExtendedConnect!.AcceptCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: Accepting accepts the tunnel once, with no Sec-WebSocket-Accept even when one was staged")]
    public async Task AcceptWebSocketAsync_ValidHandshake_AcceptsTheTunnelWithoutAcceptValue()
    {
        // Arrange — a stale accept value the application staged must not reach the 200.
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect();
        context.Response.Headers[HttpHeaderKey.SecWebSocketAccept] = "stale";
        context.Response.Headers[new HttpHeaderKey("X-Application")] = "kept";

        // Act
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

        // Assert — RFC 8441 §5: the 200 carries no accept value.
        context.ExtendedConnect!.AcceptCount.ShouldBe(1);
        context.ExtendedConnect.AcceptedHeaders.ContainsKey("Sec-WebSocket-Accept").ShouldBeFalse();
        context.ExtendedConnect.AcceptedHeaders.ContainsKey("Sec-WebSocket-Protocol").ShouldBeFalse();
        context.ExtendedConnect.AcceptedHeaders["X-Application"].ShouldBe("kept");
        socket.State.ShouldBe(WebSocketState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: An offered subprotocol is selected and sent back, as on HTTP/1.1")]
    public async Task AcceptWebSocketAsync_OfferedSubprotocol_IsSelected()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect();
        context.Request.Headers[HttpHeaderKey.SecWebSocketProtocol] = "chat, superchat";

        // Act
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new HttpWebSocketAcceptOptions { SubProtocol = "superchat" });

        // Assert
        context.WebSockets.RequestedProtocols.ShouldBe(new[] { "chat", "superchat" });
        context.ExtendedConnect!.AcceptedHeaders["Sec-WebSocket-Protocol"].ShouldBe("superchat");
        socket.SubProtocol.ShouldBe("superchat");
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: Enabled compression is negotiated, as on HTTP/1.1")]
    public async Task AcceptWebSocketAsync_CompressionEnabled_IsNegotiated()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect();
        context.Request.Headers[HttpHeaderKey.SecWebSocketExtensions] = "permessage-deflate; client_max_window_bits";

        // Act
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new HttpWebSocketAcceptOptions { DangerousEnableCompression = true });

        // Assert
        context.ExtendedConnect!.AcceptedHeaders["Sec-WebSocket-Extensions"].ShouldBe("permessage-deflate");
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: A second accept throws without accepting the tunnel again")]
    public async Task AcceptWebSocketAsync_SecondAccept_ThrowsInvalidOperationException()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect();
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(async () => await context.WebSockets.AcceptWebSocketAsync());
        context.ExtendedConnect!.AcceptCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: An unsupported version cannot be accepted")]
    public async Task AcceptWebSocketAsync_UnsupportedVersion_ThrowsWithoutAccepting()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect();
        context.Request.Headers.Remove(HttpHeaderKey.SecWebSocketVersion);

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(async () => await context.WebSockets.AcceptWebSocketAsync());
        context.ExtendedConnect!.AcceptCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Extended CONNECT: A socket accepted over the tunnel exchanges messages with a client")]
    public async Task AcceptWebSocketAsync_AcceptedOverTunnel_ExchangesMessages()
    {
        // Arrange — the tunnel is a duplex stream; the framing over it is the same as on HTTP/1.1.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        await using LoopbackStreamPair pair = await LoopbackStreamPair.CreateAsync(cancellationToken);
        WebSocketTestContext context = WebSocketTestContext.CreateExtendedConnect(pair.Server);

        using WebSocket server = await context.WebSockets.AcceptWebSocketAsync(cancellationToken: cancellationToken);
        using WebSocket client = WebSocket.CreateFromStream(pair.Client, new WebSocketCreationOptions { IsServer = false });

        // Act
        await client.SendAsync(Encoding.UTF8.GetBytes("over the tunnel"), WebSocketMessageType.Text, true, cancellationToken);
        byte[] buffer = new byte[256];
        ValueWebSocketReceiveResult received = await server.ReceiveAsync(buffer.AsMemory(), cancellationToken);
        await server.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Text, true, cancellationToken);
        ValueWebSocketReceiveResult echoed = await client.ReceiveAsync(buffer.AsMemory(), cancellationToken);

        // Assert
        received.MessageType.ShouldBe(WebSocketMessageType.Text);
        Encoding.UTF8.GetString(buffer, 0, echoed.Count).ShouldBe("over the tunnel");
    }

    private static void SetOrRemove(IHttpHeaderCollection headers, HttpHeaderKey key, string? value)
    {
        if (value is null)
        {
            headers.Remove(key);
        }
        else
        {
            headers[key] = value;
        }
    }
}
