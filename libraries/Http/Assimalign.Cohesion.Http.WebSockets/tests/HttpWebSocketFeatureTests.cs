using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.WebSockets.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.WebSockets.Tests;

/// <summary>
/// <c>context.WebSockets</c> over a context whose protocol-upgrade feature is a double: handshake
/// validation, the refusals RFC 6455 §4.2 prescribes, and the accept.
/// </summary>
public class HttpWebSocketFeatureTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: An ordinary request is not a WebSocket request and installs nothing")]
    public void WebSockets_OrdinaryRequest_ReturnsNoneWithoutInstallingAFeature()
    {
        // Arrange
        WebSocketTestContext context = new(HttpMethod.Get, HttpVersion.Http11);

        // Act
        IHttpWebSocketFeature webSockets = context.WebSockets;

        // Assert
        webSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.None);
        webSockets.IsWebSocketRequest.ShouldBeFalse();
        webSockets.RequestedProtocols.ShouldBeEmpty();
        context.Features.Get<IHttpWebSocketFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: A valid handshake is validated once and installed for later reads")]
    public void WebSockets_ValidHandshake_IsValidAndInstalledOnce()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();

        // Act
        IHttpWebSocketFeature first = context.WebSockets;
        IHttpWebSocketFeature second = context.WebSockets;

        // Assert
        first.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.Valid);
        first.IsWebSocketRequest.ShouldBeTrue();
        second.ShouldBeSameAs(first);
        context.Features.Get<IHttpWebSocketFeature>().ShouldBeSameAs(first);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: The Upgrade token is matched case-insensitively")]
    public void WebSockets_MixedCaseUpgradeToken_IsAHandshake()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake(upgradeProtocol: "WebSocket");

        // Act / Assert
        context.WebSockets.IsWebSocketRequest.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: An upgrade to another protocol is not a WebSocket request")]
    public void WebSockets_UpgradeToAnotherProtocol_ReturnsNone()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake(upgradeProtocol: "h2c");

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.None);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: A non-GET handshake is invalid")]
    public void WebSockets_PostHandshake_IsInvalid()
    {
        // Arrange
        WebSocketTestContext context = new(HttpMethod.Post, HttpVersion.Http11);
        context.Request.Headers[HttpHeaderKey.SecWebSocketKey] = WebSocketTestContext.SampleKey;
        context.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "13";
        context.InstallUpgrade("websocket", new MemoryStream());

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.Invalid);
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: A handshake that carries content is invalid; an empty one is not")]
    [InlineData("Content-Length", "5", HttpWebSocketHandshakeStatus.Invalid)]
    [InlineData("Transfer-Encoding", "chunked", HttpWebSocketHandshakeStatus.Invalid)]
    [InlineData("Content-Length", "0", HttpWebSocketHandshakeStatus.Valid)]
    public void WebSockets_HandshakeContent_IsInvalidUnlessEmpty(string header, string value, HttpWebSocketHandshakeStatus expected)
    {
        // Arrange — after the switch every octet is the WebSocket's, so a body would be read as frames.
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers[new HttpHeaderKey(header)] = value;

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: A missing or malformed key is invalid")]
    [InlineData(null)]
    [InlineData("not-a-key")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAA=")]
    public void WebSockets_BadKey_IsInvalid(string? key)
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        SetOrRemove(context.Request.Headers, HttpHeaderKey.SecWebSocketKey, key);

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.Invalid);
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: A version other than 13, or none, is unsupported")]
    [InlineData(null)]
    [InlineData("8")]
    [InlineData("7")]
    public void WebSockets_OtherVersion_IsUnsupportedVersion(string? version)
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        SetOrRemove(context.Request.Headers, HttpHeaderKey.SecWebSocketVersion, version);

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.UnsupportedVersion);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: An unsupported version is reported before a missing key")]
    public void WebSockets_EarlierDraftClient_IsUnsupportedVersion()
    {
        // Arrange — a client of an earlier draft sends neither the version nor the key.
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers.Remove(HttpHeaderKey.SecWebSocketVersion);
        context.Request.Headers.Remove(HttpHeaderKey.SecWebSocketKey);

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.UnsupportedVersion);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: A malformed subprotocol list makes the handshake invalid")]
    public void WebSockets_MalformedSubprotocolList_IsInvalid()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketProtocol] = "chat room";

        // Act / Assert
        context.WebSockets.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.Invalid);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: The offered subprotocols are exposed in order")]
    public void WebSockets_OfferedSubprotocols_AreExposedInOrder()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketProtocol] = "chat, superchat";

        // Act / Assert
        context.WebSockets.RequestedProtocols.ShouldBe(new[] { "chat", "superchat" });
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: A feature installed by a policy layer wins over the default")]
    public void WebSockets_InstalledFeature_IsReturned()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        IHttpWebSocketFeature inner = context.WebSockets;
        DecoratingFeature decorator = new(inner);
        context.Features.Set(decorator);

        // Act / Assert
        context.WebSockets.ShouldBeSameAs(decorator);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - WebSockets: A null context throws")]
    public void WebSockets_NullContext_ThrowsArgumentNullException()
    {
        IHttpContext context = null!;

        Should.Throw<ArgumentNullException>(() => { _ = context.WebSockets; });
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - RejectHandshake: An unsupported version is refused with 426 and the supported version")]
    public void RejectHandshake_UnsupportedVersion_Stages426WithVersion13()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "8";

        // Act
        context.WebSockets.RejectHandshake();

        // Assert — RFC 6455 §4.4, and RFC 9110 §15.5.22's Upgrade with its connection option.
        context.Response.StatusCode.ShouldBe(HttpStatusCode.UpgradeRequired);
        context.ResponseHeader(HttpHeaderKey.SecWebSocketVersion).ShouldBe("13");
        context.ResponseHeader(HttpHeaderKey.Upgrade).ShouldBe("websocket");
        context.ResponseHeader(HttpHeaderKey.Connection).ShouldBe("Upgrade");
        context.Upgrade!.AcceptCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - RejectHandshake: A malformed handshake is refused with 400")]
    public void RejectHandshake_BadKey_Stages400()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketKey] = "short";

        // Act
        context.WebSockets.RejectHandshake();

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        context.ResponseHeader(HttpHeaderKey.SecWebSocketVersion).ShouldBeNull();
        context.Upgrade!.AcceptCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - RejectHandshake: A valid handshake or an ordinary request cannot be refused")]
    public void RejectHandshake_ValidOrNoHandshake_ThrowsInvalidOperationException()
    {
        // Arrange
        WebSocketTestContext valid = WebSocketTestContext.CreateHandshake();
        WebSocketTestContext ordinary = new(HttpMethod.Get, HttpVersion.Http11);

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => valid.WebSockets.RejectHandshake());
        Should.Throw<InvalidOperationException>(() => ordinary.WebSockets.RejectHandshake());
        valid.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptWebSocketAsync: An invalid handshake cannot be accepted")]
    public async Task AcceptWebSocketAsync_InvalidHandshake_ThrowsWithoutSwitching()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "8";

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(async () => await context.WebSockets.AcceptWebSocketAsync());
        context.Upgrade!.AcceptCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptWebSocketAsync: Sec-WebSocket-Accept is staged before the protocol switch")]
    public async Task AcceptWebSocketAsync_ValidHandshake_StagesAcceptValueBeforeSwitching()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();

        // Act
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

        // Assert — the values the 101 carries.
        context.Upgrade!.AcceptCount.ShouldBe(1);
        context.Upgrade.AcceptedHeaders["Sec-WebSocket-Accept"].ShouldBe(WebSocketTestContext.SampleAccept);
        context.Upgrade.AcceptedHeaders.ContainsKey("Sec-WebSocket-Protocol").ShouldBeFalse();
        context.Upgrade.AcceptedHeaders.ContainsKey("Sec-WebSocket-Extensions").ShouldBeFalse();
        socket.State.ShouldBe(WebSocketState.Open);
        socket.SubProtocol.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptWebSocketAsync: A second accept throws without switching again")]
    public async Task AcceptWebSocketAsync_SecondAccept_ThrowsInvalidOperationException()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

        // Act / Assert
        await Should.ThrowAsync<InvalidOperationException>(async () => await context.WebSockets.AcceptWebSocketAsync());
        context.Upgrade!.AcceptCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptWebSocketAsync: An offered subprotocol is selected and sent back")]
    public async Task AcceptWebSocketAsync_OfferedSubprotocol_IsSelected()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketProtocol] = "chat, superchat";

        // Act
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new HttpWebSocketAcceptOptions { SubProtocol = "superchat" });

        // Assert
        context.Upgrade!.AcceptedHeaders["Sec-WebSocket-Protocol"].ShouldBe("superchat");
        socket.SubProtocol.ShouldBe("superchat");
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptWebSocketAsync: A subprotocol the client did not offer is rejected, and the handshake stays acceptable")]
    public async Task AcceptWebSocketAsync_UnofferedSubprotocol_ThrowsArgumentExceptionWithoutSwitching()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketProtocol] = "chat";

        // Act / Assert — subprotocols compare case-sensitively.
        await Should.ThrowAsync<ArgumentException>(async () =>
            await context.WebSockets.AcceptWebSocketAsync(new HttpWebSocketAcceptOptions { SubProtocol = "Chat" }));
        context.Upgrade!.AcceptCount.ShouldBe(0);

        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new HttpWebSocketAcceptOptions { SubProtocol = "chat" });
        socket.SubProtocol.ShouldBe("chat");
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptWebSocketAsync: Handshake fields the application staged are replaced by the negotiated ones")]
    public async Task AcceptWebSocketAsync_StaleHandshakeFields_AreReplaced()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Response.Headers[HttpHeaderKey.SecWebSocketAccept] = "stale";
        context.Response.Headers[HttpHeaderKey.SecWebSocketProtocol] = "never-offered";
        context.Response.Headers[HttpHeaderKey.SecWebSocketExtensions] = "permessage-deflate";
        context.Response.Headers[new HttpHeaderKey("X-Application")] = "kept";

        // Act
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

        // Assert
        context.Upgrade!.AcceptedHeaders["Sec-WebSocket-Accept"].ShouldBe(WebSocketTestContext.SampleAccept);
        context.Upgrade.AcceptedHeaders.ContainsKey("Sec-WebSocket-Protocol").ShouldBeFalse();
        context.Upgrade.AcceptedHeaders.ContainsKey("Sec-WebSocket-Extensions").ShouldBeFalse();
        context.Upgrade.AcceptedHeaders["X-Application"].ShouldBe("kept");
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptWebSocketAsync: A compression offer is declined unless compression is enabled")]
    public async Task AcceptWebSocketAsync_CompressionOfferWithoutOptIn_IsDeclined()
    {
        // Arrange
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketExtensions] = "permessage-deflate; client_max_window_bits";

        // Act
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(new HttpWebSocketAcceptOptions { DangerousEnableCompression = false });

        // Assert
        context.Upgrade!.AcceptedHeaders.ContainsKey("Sec-WebSocket-Extensions").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptWebSocketAsync: An accepted socket exchanges messages with a client")]
    public async Task AcceptWebSocketAsync_AcceptedSocket_ExchangesMessages()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        await using LoopbackStreamPair pair = await LoopbackStreamPair.CreateAsync(cancellationToken);
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake(pair.Server);

        using WebSocket server = await context.WebSockets.AcceptWebSocketAsync(cancellationToken: cancellationToken);
        using WebSocket client = WebSocket.CreateFromStream(pair.Client, new WebSocketCreationOptions { IsServer = false });

        // Act
        await client.SendAsync(Encoding.UTF8.GetBytes("ping from the client"), WebSocketMessageType.Text, true, cancellationToken);
        byte[] buffer = new byte[256];
        ValueWebSocketReceiveResult received = await server.ReceiveAsync(buffer.AsMemory(), cancellationToken);
        await server.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Text, true, cancellationToken);
        ValueWebSocketReceiveResult echoed = await client.ReceiveAsync(buffer.AsMemory(), cancellationToken);

        // Assert
        received.MessageType.ShouldBe(WebSocketMessageType.Text);
        echoed.EndOfMessage.ShouldBeTrue();
        Encoding.UTF8.GetString(buffer, 0, echoed.Count).ShouldBe("ping from the client");
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptWebSocketAsync: Enabled compression is negotiated and compresses the server's messages")]
    public async Task AcceptWebSocketAsync_CompressionEnabled_NegotiatesAndCompresses()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        await using LoopbackStreamPair pair = await LoopbackStreamPair.CreateAsync(cancellationToken);
        WebSocketTestContext context = WebSocketTestContext.CreateHandshake(pair.Server);
        context.Request.Headers[HttpHeaderKey.SecWebSocketExtensions] = "permessage-deflate; client_max_window_bits";

        // Act
        using WebSocket server = await context.WebSockets.AcceptWebSocketAsync(
            new HttpWebSocketAcceptOptions { DangerousEnableCompression = true },
            cancellationToken);
        await server.SendAsync(Encoding.UTF8.GetBytes(new string('a', 512)), WebSocketMessageType.Text, true, cancellationToken);

        byte[] header = new byte[2];
        await pair.Client.ReadExactlyAsync(header, cancellationToken);

        // Assert — the response announced the extension, and the frame carries RSV1, the
        // compressed-message bit, with a payload far shorter than the 512-byte message.
        context.Upgrade!.AcceptedHeaders["Sec-WebSocket-Extensions"].ShouldBe("permessage-deflate");
        (header[0] & 0x40).ShouldBe(0x40);
        (header[1] & 0x7F).ShouldBeLessThan(126);
    }

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptOptions: A negative keep-alive interval or timeout is rejected")]
    public void AcceptOptions_NegativeKeepAlive_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        HttpWebSocketAcceptOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.KeepAliveInterval = TimeSpan.FromSeconds(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => options.KeepAliveTimeout = TimeSpan.FromSeconds(-1));
        options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        options.KeepAliveTimeout = TimeSpan.Zero;
        options.KeepAliveInterval.ShouldBe(Timeout.InfiniteTimeSpan);
        options.KeepAliveTimeout.ShouldBe(TimeSpan.Zero);
    }

    [Theory(DisplayName = "Cohesion Test [Http.WebSockets] - AcceptOptions: A server window outside 9 to 15 bits is rejected")]
    [InlineData(8)]
    [InlineData(16)]
    public void AcceptOptions_ServerWindowOutOfRange_ThrowsArgumentOutOfRangeException(int bits)
    {
        HttpWebSocketAcceptOptions options = new();

        Should.Throw<ArgumentOutOfRangeException>(() => options.ServerMaxWindowBits = bits);
        options.ServerMaxWindowBits.ShouldBe(15);
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

    private sealed class DecoratingFeature : IHttpWebSocketFeature
    {
        private readonly IHttpWebSocketFeature _inner;

        public DecoratingFeature(IHttpWebSocketFeature inner) => _inner = inner;

        public string Name => _inner.Name;

        public HttpWebSocketHandshakeStatus HandshakeStatus => _inner.HandshakeStatus;

        public bool IsWebSocketRequest => _inner.IsWebSocketRequest;

        public System.Collections.Generic.IReadOnlyList<string> RequestedProtocols => _inner.RequestedProtocols;

        public ValueTask<WebSocket> AcceptWebSocketAsync(HttpWebSocketAcceptOptions? options = null, CancellationToken cancellationToken = default)
            => _inner.AcceptWebSocketAsync(options, cancellationToken);

        public void RejectHandshake() => _inner.RejectHandshake();
    }
}
