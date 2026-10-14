using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.WebSockets.Tests;

/// <summary>
/// WebSockets end to end over real loopback HTTP/1.1: the default Web server (its upgrade
/// interceptor and drain signal), <c>UseWebSockets</c>, and an echo endpoint written the way an
/// application writes one. The BCL client proves interoperability; a raw socket proves the
/// server's RFC 6455 and RFC 7692 behavior frame by frame.
/// </summary>
public class WebSocketEndToEndTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A text message is echoed to the BCL client")]
    public async Task UseWebSockets_TextMessage_IsEchoed()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        using ClientWebSocket client = new();

        // Act
        await client.ConnectAsync(server.WebSocketUri(), cancellationToken);
        await client.SendAsync(Encoding.UTF8.GetBytes("héllo, wörld"), WebSocketMessageType.Text, true, cancellationToken);
        (WebSocketMessageType type, byte[] message) = await ReceiveMessageAsync(client, cancellationToken);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);

        // Assert
        type.ShouldBe(WebSocketMessageType.Text);
        Encoding.UTF8.GetString(message).ShouldBe("héllo, wörld");
        client.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        (await endpoint.Completed.WaitAsync(cancellationToken)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A large binary message is echoed intact")]
    public async Task UseWebSockets_LargeBinaryMessage_IsEchoed()
    {
        // Arrange — larger than a 16-bit length, so the 64-bit length form crosses the wire.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        using ClientWebSocket client = new();
        byte[] payload = new byte[70_000];
        new Random(765).NextBytes(payload);

        // Act
        await client.ConnectAsync(server.WebSocketUri(), cancellationToken);
        await client.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
        (WebSocketMessageType type, byte[] message) = await ReceiveMessageAsync(client, cancellationToken);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

        // Assert
        type.ShouldBe(WebSocketMessageType.Binary);
        message.SequenceEqual(payload).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: The endpoint selects a subprotocol the client offered")]
    public async Task UseWebSockets_OfferedSubprotocol_IsSelected()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new() { SelectFirstSubProtocol = true };
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        using ClientWebSocket client = new();
        client.Options.AddSubProtocol("chat.v2");
        client.Options.AddSubProtocol("chat.v1");

        // Act
        await client.ConnectAsync(server.WebSocketUri(), cancellationToken);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

        // Assert
        client.SubProtocol.ShouldBe("chat.v2");
        endpoint.RequestedProtocols.ShouldBe(new[] { "chat.v2", "chat.v1" });
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: An unmasked client frame closes the connection with 1002")]
    public async Task RawFrame_UnmaskedClientFrame_ClosesWith1002()
    {
        // RFC 6455 §5.1: the server MUST close the connection on an unmasked client frame.
        RawFrame close = await SendRawFrameAsync(0x81, Encoding.ASCII.GetBytes("unmasked frame"), masked: false);

        close.Opcode.ShouldBe(0x8);
        close.CloseCode.ShouldBe(1002);
        close.IsMasked.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: Invalid UTF-8 in a text message closes the connection with 1007")]
    public async Task RawFrame_InvalidUtf8Text_ClosesWith1007()
    {
        // RFC 6455 §8.1: a text message that is not valid UTF-8 fails the connection with 1007.
        RawFrame close = await SendRawFrameAsync(0x81, new byte[] { 0x68, 0xC3, 0x28, 0x69 });

        close.Opcode.ShouldBe(0x8);
        close.CloseCode.ShouldBe(1007);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A control frame longer than 125 bytes closes the connection with 1002")]
    public async Task RawFrame_ControlFrameOver125Bytes_ClosesWith1002()
    {
        // RFC 6455 §5.5: every control frame MUST have a payload of 125 bytes or less.
        RawFrame close = await SendRawFrameAsync(0x89, new byte[126]);

        close.Opcode.ShouldBe(0x8);
        close.CloseCode.ShouldBe(1002);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A fragmented control frame closes the connection with 1002")]
    public async Task RawFrame_FragmentedControlFrame_ClosesWith1002()
    {
        // RFC 6455 §5.5: control frames MUST NOT be fragmented (a ping without FIN).
        RawFrame close = await SendRawFrameAsync(0x09, new byte[] { 1, 2, 3 });

        close.Opcode.ShouldBe(0x8);
        close.CloseCode.ShouldBe(1002);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A frame with RSV1 set but no compression negotiated closes the connection with 1002")]
    public async Task RawFrame_Rsv1WithoutCompression_ClosesWith1002()
    {
        // RFC 6455 §5.2: a reserved bit no extension defines fails the connection.
        RawFrame close = await SendRawFrameAsync(0xC1, Encoding.ASCII.GetBytes("not compressed"));

        close.Opcode.ShouldBe(0x8);
        close.CloseCode.ShouldBe(1002);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A ping is answered with a pong echoing its payload")]
    public async Task RawFrame_Ping_IsAnsweredWithPongEchoingPayload()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);
        (await client.HandshakeAsync(cancellationToken)).StatusCode.ShouldBe(101);

        // Act — RFC 6455 §5.5.3: the Pong carries the Ping's application data.
        await client.SendFrameAsync(0x89, Encoding.ASCII.GetBytes("are you there?"), cancellationToken);
        RawFrame pong = await client.ReadFrameAsync(cancellationToken);

        // Assert
        pong.Opcode.ShouldBe(0xA);
        pong.IsFinal.ShouldBeTrue();
        Encoding.ASCII.GetString(pong.Payload).ShouldBe("are you there?");
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A close with a valid code completes the close handshake with that code")]
    public async Task RawFrame_CloseWithValidCode_IsAnsweredWithTheSameCode()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);
        (await client.HandshakeAsync(cancellationToken)).StatusCode.ShouldBe(101);

        // Act — RFC 6455 §5.5.1: the endpoint answers a close with a close.
        await client.SendFrameAsync(0x88, new byte[] { 0x03, 0xE8, (byte)'b', (byte)'y', (byte)'e' }, cancellationToken);
        RawFrame close = await client.ReadFrameAsync(cancellationToken);

        // Assert
        close.Opcode.ShouldBe(0x8);
        close.CloseCode.ShouldBe(1000);
        (await endpoint.Completed.WaitAsync(cancellationToken)).ShouldBeNull();
        (await client.ReadRemainingAsync(TimeSpan.FromSeconds(5), cancellationToken)).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A close with a reserved code closes the connection with 1002")]
    public async Task RawFrame_CloseWithReservedCode_ClosesWith1002()
    {
        // RFC 6455 §7.4.1: 1005 is reserved and MUST NOT be sent in a close frame.
        RawFrame close = await SendRawFrameAsync(0x88, new byte[] { 0x03, 0xED });

        close.Opcode.ShouldBe(0x8);
        close.CloseCode.ShouldBe(1002);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: Enabled permessage-deflate is negotiated and round-trips with the BCL client")]
    public async Task UseWebSockets_CompressionEnabled_RoundTripsWithTheBclClient()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            options => options.DangerousEnableCompression = true,
            endpoint.InvokeAsync,
            cancellationToken);
        using ClientWebSocket client = new();
        client.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        client.Options.CollectHttpResponseDetails = true;
        string text = string.Concat(Enumerable.Repeat("compress me, ", 200));

        // Act
        await client.ConnectAsync(server.WebSocketUri(), cancellationToken);
        await client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
        (WebSocketMessageType type, byte[] message) = await ReceiveMessageAsync(client, cancellationToken);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

        // Assert
        client.HttpResponseHeaders!["Sec-WebSocket-Extensions"].Single().ShouldStartWith("permessage-deflate");
        type.ShouldBe(WebSocketMessageType.Text);
        Encoding.UTF8.GetString(message).ShouldBe(text);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: With permessage-deflate negotiated, the server sends compressed frames and reads compressed ones")]
    public async Task RawFrame_CompressionNegotiated_ServerCompressesAndInflates()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            options => options.DangerousEnableCompression = true,
            endpoint.InvokeAsync,
            cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);
        RawHandshakeResponse response = await client.HandshakeAsync(cancellationToken, extensions: "permessage-deflate; client_no_context_takeover");
        string text = string.Concat(Enumerable.Repeat("deflate ", 64));

        // Act — RFC 7692 §7.2: a compressed message sets RSV1 and drops the final 0x00 0x00 0xFF 0xFF.
        await client.SendFrameAsync(0xC1, Deflate(Encoding.ASCII.GetBytes(text)), cancellationToken);
        RawFrame echo = await client.ReadFrameAsync(cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(101);
        response.Header("Sec-WebSocket-Extensions").ShouldBe("permessage-deflate; client_no_context_takeover");
        echo.Opcode.ShouldBe(0x1);
        echo.IsCompressed.ShouldBeTrue();
        echo.Payload.Length.ShouldBeLessThan(text.Length);
        Encoding.ASCII.GetString(Inflate(echo.Payload)).ShouldBe(text);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A compression offer is declined while compression is not enabled")]
    public async Task UseWebSockets_CompressionNotEnabled_DeclinesTheOffer()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);

        // Act
        RawHandshakeResponse response = await client.HandshakeAsync(cancellationToken, extensions: "permessage-deflate; client_max_window_bits");
        await client.SendFrameAsync(0x81, Encoding.ASCII.GetBytes("plain"), cancellationToken);
        RawFrame echo = await client.ReadFrameAsync(cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(101);
        response.Header("Sec-WebSocket-Extensions").ShouldBeNull();
        echo.IsCompressed.ShouldBeFalse();
        Encoding.ASCII.GetString(echo.Payload).ShouldBe("plain");
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: The 101 carries the RFC 6455 accept value")]
    public async Task Handshake_ValidRequest_Answers101WithAcceptValue()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);

        // Act
        RawHandshakeResponse response = await client.HandshakeAsync(cancellationToken);

        // Assert — RFC 6455 §1.3's sample key and accept value.
        response.StatusCode.ShouldBe(101);
        response.Header("Sec-WebSocket-Accept").ShouldBe("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
        response.Header("Upgrade").ShouldBe("websocket", StringCompareShould.IgnoreCase);
        response.Header("Connection").ShouldBe("Upgrade", StringCompareShould.IgnoreCase);
        response.Header("Content-Length").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: Another WebSocket version is refused with 426 and Sec-WebSocket-Version: 13")]
    public async Task Handshake_UnsupportedVersion_Answers426()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);

        // Act
        RawHandshakeResponse response = await client.HandshakeAsync(cancellationToken, version: "8");

        // Assert
        response.StatusCode.ShouldBe(426);
        response.Header("Sec-WebSocket-Version").ShouldBe("13");
        response.Header("Upgrade").ShouldBe("websocket");
        endpoint.Completed.IsCompleted.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A malformed handshake is refused with 400")]
    [InlineData("GET", "not-a-key")]
    [InlineData("GET", null)]
    [InlineData("POST", RawWebSocketClient.SampleKey)]
    public async Task Handshake_Malformed_Answers400(string method, string? key)
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);

        // Act
        RawHandshakeResponse response = await client.HandshakeAsync(cancellationToken, method: method, key: key);

        // Assert
        response.StatusCode.ShouldBe(400);
        response.Header("Sec-WebSocket-Accept").ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A cross-site handshake is refused with 403")]
    public async Task Origin_CrossSite_Answers403()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);

        // Act
        RawHandshakeResponse response = await client.HandshakeAsync(cancellationToken, origin: "https://attacker.example");

        // Assert
        response.StatusCode.ShouldBe(403);
        endpoint.Completed.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A cross-site handshake from the BCL client fails with the 403")]
    public async Task Origin_CrossSiteBclClient_FailsWith403()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        using ClientWebSocket client = new();
        client.Options.SetRequestHeader("Origin", "https://attacker.example");
        client.Options.CollectHttpResponseDetails = true;

        // Act / Assert
        await Should.ThrowAsync<WebSocketException>(() => client.ConnectAsync(server.WebSocketUri(), cancellationToken));
        client.HttpStatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: An allowed origin opens a socket")]
    public async Task Origin_Allowed_Answers101()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            options => options.AllowedOrigins.Add("https://chat.example"),
            endpoint.InvokeAsync,
            cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);

        // Act
        RawHandshakeResponse response = await client.HandshakeAsync(cancellationToken, origin: "https://chat.example");

        // Assert
        response.StatusCode.ShouldBe(101);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A same-origin handshake opens a socket")]
    public async Task Origin_SameOrigin_Answers101()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        using ClientWebSocket client = new();
        client.Options.SetRequestHeader("Origin", server.Origin);

        // Act
        await client.ConnectAsync(server.WebSocketUri(), cancellationToken);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

        // Assert
        client.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: A handshake without Origin opens a socket")]
    public async Task Origin_None_Answers101()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);

        // Act
        RawHandshakeResponse response = await client.HandshakeAsync(cancellationToken, origin: null);

        // Assert
        response.StatusCode.ShouldBe(101);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: The policy's keep-alive interval applies to the accepted socket")]
    public async Task UseWebSockets_KeepAliveInterval_SendsKeepAliveFrames()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            options => options.KeepAliveInterval = TimeSpan.FromMilliseconds(200),
            endpoint.InvokeAsync,
            cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);
        (await client.HandshakeAsync(cancellationToken)).StatusCode.ShouldBe(101);

        // Act — the client sends nothing; with no keep-alive timeout the BCL sends unsolicited pongs.
        RawFrame keepAlive = await client.ReadFrameAsync(cancellationToken);

        // Assert
        keepAlive.Opcode.ShouldBe(0xA);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end: Stopping the server closes an open socket with 1001 well before the budget runs out")]
    public async Task StopAsync_OpenSocket_ClosesWith1001BeforeTheBudget()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        using ClientWebSocket client = new();
        await client.ConnectAsync(server.WebSocketUri(), cancellationToken);
        await client.SendAsync(Encoding.UTF8.GetBytes("before the stop"), WebSocketMessageType.Text, true, cancellationToken);
        await ReceiveMessageAsync(client, cancellationToken);

        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(20));
        Stopwatch elapsed = Stopwatch.StartNew();

        // Act — the drain sends 1001; the client answers it as any client does.
        Task stop = server.Server.StopAsync(budget.Token);
        WebSocketReceiveResult close = await client.ReceiveAsync(new ArraySegment<byte>(new byte[256]), cancellationToken);
        await client.CloseOutputAsync(close.CloseStatus!.Value, close.CloseStatusDescription, cancellationToken);
        await stop.WaitAsync(cancellationToken);

        // Assert — a clean close inside the budget, and the endpoint's own CloseAsync did not throw.
        close.MessageType.ShouldBe(WebSocketMessageType.Close);
        close.CloseStatus.ShouldBe(WebSocketCloseStatus.EndpointUnavailable);
        budget.IsCancellationRequested.ShouldBeFalse();
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        (await endpoint.Completed.WaitAsync(cancellationToken)).ShouldBeNull();
    }

    /// <summary>
    /// Opens a socket with a raw client, sends one frame, and returns the frame the server answers
    /// with, after asserting that the endpoint's receive failed rather than completing a message.
    /// </summary>
    private static async Task<RawFrame> SendRawFrameAsync(byte firstByte, byte[] payload, bool masked = true)
    {
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(null, endpoint.InvokeAsync, cancellationToken);
        await using RawWebSocketClient client = await RawWebSocketClient.ConnectAsync(server.Port, cancellationToken);
        (await client.HandshakeAsync(cancellationToken)).StatusCode.ShouldBe(101);

        await client.SendFrameAsync(firstByte, payload, cancellationToken, masked);
        RawFrame frame = await client.ReadFrameAsync(cancellationToken);

        (await endpoint.Completed.WaitAsync(cancellationToken)).ShouldBeOfType<WebSocketException>();
        return frame;
    }

    private static async Task<(WebSocketMessageType Type, byte[] Message)> ReceiveMessageAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using MemoryStream message = new();
        byte[] buffer = new byte[8192];
        ValueWebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (result.MessageType, message.ToArray());
    }

    private static byte[] Deflate(byte[] data)
    {
        using MemoryStream output = new();
        using (DeflateStream deflate = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
            deflate.Flush();
        }

        byte[] compressed = output.ToArray();

        // A sync flush ends with an empty stored block, 0x00 0x00 0xFF 0xFF, which the extension
        // drops; disposing the stream added a final empty block after it, which is cut too.
        int end = FindTail(compressed);
        return compressed[..end];
    }

    private static int FindTail(byte[] compressed)
    {
        for (int i = compressed.Length - 4; i >= 0; i--)
        {
            if (compressed[i] == 0x00 && compressed[i + 1] == 0x00 && compressed[i + 2] == 0xFF && compressed[i + 3] == 0xFF)
            {
                return i;
            }
        }

        return compressed.Length;
    }

    private static byte[] Inflate(byte[] payload)
    {
        using MemoryStream input = new();
        input.Write(payload);
        input.Write(new byte[] { 0x00, 0x00, 0xFF, 0xFF });
        input.Position = 0;

        using DeflateStream inflate = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[4096];
        int read;

        try
        {
            while ((read = inflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException) when (output.Length > 0)
        {
            // The tail is a sync flush, not a final block, so the stream ends without one.
        }

        return output.ToArray();
    }
}
