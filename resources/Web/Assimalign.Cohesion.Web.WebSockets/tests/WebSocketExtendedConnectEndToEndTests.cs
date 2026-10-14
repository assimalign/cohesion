using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

using Shouldly;

using Xunit;

using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.WebSockets.Tests;

/// <summary>
/// WebSockets end to end over HTTP/2 (RFC 8441) and HTTP/3 (RFC 9220): the opening handshake is an
/// extended CONNECT, answered <c>200</c> with no accept value, and <c>UseWebSockets</c>' origin check,
/// keep-alive defaults and drain apply exactly as on HTTP/1.1. HTTP/2 is driven by the BCL
/// <see cref="ClientWebSocket"/> over prior-knowledge cleartext HTTP/2 on loopback TCP; HTTP/3 by a
/// minimal client over the in-memory multiplexed driver (<see cref="Http3TestClient"/>), since the
/// BCL client does not speak HTTP/3, with the BCL WebSocket framing over the request stream.
/// </summary>
public class WebSocketExtendedConnectEndToEndTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end/Http2: Messages are echoed over an extended CONNECT answered 200 without an accept value")]
    public async Task Http2_Messages_AreEchoed()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(WebSocketTestProtocol.Http2, null, endpoint.InvokeAsync, cancellationToken);
        using HttpMessageInvoker invoker = new(new SocketsHttpHandler());
        using ClientWebSocket client = CreateHttp2Client();
        byte[] binary = new byte[70_000];
        new Random(8441).NextBytes(binary);

        // Act
        await client.ConnectAsync(server.WebSocketUri(), invoker, cancellationToken);
        await client.SendAsync(Encoding.UTF8.GetBytes("héllo over h2"), WebSocketMessageType.Text, true, cancellationToken);
        (WebSocketMessageType textType, byte[] text) = await ReceiveMessageAsync(client, cancellationToken);
        await client.SendAsync(binary, WebSocketMessageType.Binary, true, cancellationToken);
        (WebSocketMessageType binaryType, byte[] echoedBinary) = await ReceiveMessageAsync(client, cancellationToken);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);

        // Assert — RFC 8441 §5: a 200, and neither the HTTP/1.1 accept value nor its upgrade fields.
        client.HttpStatusCode.ShouldBe(NetHttpStatusCode.OK);
        client.HttpResponseHeaders!.ContainsKey("Sec-WebSocket-Accept").ShouldBeFalse();
        textType.ShouldBe(WebSocketMessageType.Text);
        Encoding.UTF8.GetString(text).ShouldBe("héllo over h2");
        binaryType.ShouldBe(WebSocketMessageType.Binary);
        echoedBinary.AsSpan().SequenceEqual(binary).ShouldBeTrue();
        client.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        (await endpoint.Completed.WaitAsync(cancellationToken)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end/Http2: A subprotocol and permessage-deflate are negotiated as on HTTP/1.1")]
    public async Task Http2_SubprotocolAndCompression_AreNegotiated()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new() { SelectFirstSubProtocol = true };
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(
            WebSocketTestProtocol.Http2,
            options => options.DangerousEnableCompression = true,
            endpoint.InvokeAsync,
            cancellationToken);
        using HttpMessageInvoker invoker = new(new SocketsHttpHandler());
        using ClientWebSocket client = CreateHttp2Client();
        client.Options.AddSubProtocol("chat.v2");
        client.Options.AddSubProtocol("chat.v1");
        client.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();
        string text = string.Concat(Enumerable.Repeat("compress me over h2, ", 200));

        // Act
        await client.ConnectAsync(server.WebSocketUri(), invoker, cancellationToken);
        await client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
        (_, byte[] message) = await ReceiveMessageAsync(client, cancellationToken);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

        // Assert
        client.SubProtocol.ShouldBe("chat.v2");
        endpoint.RequestedProtocols.ShouldBe(new[] { "chat.v2", "chat.v1" });
        client.HttpResponseHeaders!["Sec-WebSocket-Extensions"].Single().ShouldStartWith("permessage-deflate");
        Encoding.UTF8.GetString(message).ShouldBe(text);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end/Http2: A cross-site handshake from the BCL client fails with the 403")]
    public async Task Http2_CrossSiteOrigin_FailsWith403()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(WebSocketTestProtocol.Http2, null, endpoint.InvokeAsync, cancellationToken);
        using HttpMessageInvoker invoker = new(new SocketsHttpHandler());
        using ClientWebSocket client = CreateHttp2Client();
        client.Options.SetRequestHeader("Origin", "https://attacker.example");

        // Act / Assert
        await Should.ThrowAsync<WebSocketException>(() => client.ConnectAsync(server.WebSocketUri(), invoker, cancellationToken));
        client.HttpStatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        endpoint.Completed.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end/Http2: Another WebSocket version is refused with 426 and Sec-WebSocket-Version: 13, without Upgrade or Connection")]
    public async Task Http2_UnsupportedVersion_Answers426WithoutConnectionSpecificFields()
    {
        // Arrange — the BCL client always sends version 13, so the extended CONNECT is sent by hand.
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(WebSocketTestProtocol.Http2, null, endpoint.InvokeAsync, cancellationToken);
        using HttpClient http = new(new SocketsHttpHandler());
        using HttpRequestMessage request = new(NetHttpMethod.Connect, $"http://127.0.0.1:{server.Port}/ws")
        {
            Version = NetHttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Protocol = "websocket";
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "8");

        // Act — an HTTP/2 client treats a response carrying a connection-specific field as malformed
        // (RFC 9113 §8.2.2), so the response arriving at all already says the fields are absent.
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.UpgradeRequired);
        response.Version.ShouldBe(NetHttpVersion.Version20);
        response.Headers.GetValues("Sec-WebSocket-Version").Single().ShouldBe("13");
        response.Headers.Upgrade.ShouldBeEmpty();
        response.Headers.Connection.ShouldBeEmpty();
        endpoint.Completed.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end/Http2: Stopping the server closes an open socket with 1001 well before the budget runs out")]
    public async Task Http2_StopAsync_ClosesWith1001BeforeTheBudget()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(WebSocketTestProtocol.Http2, null, endpoint.InvokeAsync, cancellationToken);
        using HttpMessageInvoker invoker = new(new SocketsHttpHandler());
        using ClientWebSocket client = CreateHttp2Client();
        await client.ConnectAsync(server.WebSocketUri(), invoker, cancellationToken);
        await client.SendAsync(Encoding.UTF8.GetBytes("before the stop"), WebSocketMessageType.Text, true, cancellationToken);
        await ReceiveMessageAsync(client, cancellationToken);

        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(20));
        Stopwatch elapsed = Stopwatch.StartNew();

        // Act — the drain sends 1001 on the stream; the client answers it as any client does.
        Task stop = server.Server.StopAsync(budget.Token);
        WebSocketReceiveResult close = await client.ReceiveAsync(new ArraySegment<byte>(new byte[256]), cancellationToken);
        await client.CloseOutputAsync(close.CloseStatus!.Value, close.CloseStatusDescription, cancellationToken);
        await stop.WaitAsync(cancellationToken);

        // Assert
        close.MessageType.ShouldBe(WebSocketMessageType.Close);
        close.CloseStatus.ShouldBe(WebSocketCloseStatus.EndpointUnavailable);
        budget.IsCancellationRequested.ShouldBeFalse();
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        (await endpoint.Completed.WaitAsync(cancellationToken)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end/Http3: Messages are echoed over an extended CONNECT answered 200 without an accept value")]
    public async Task Http3_Messages_AreEchoed()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new() { SelectFirstSubProtocol = true };
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(WebSocketTestProtocol.Http3, null, endpoint.InvokeAsync, cancellationToken);
        await using Http3TestClient client = await Http3TestClient.ConnectAsync(server.Http3Listener!, cancellationToken);

        // Act
        (Http3RequestStream stream, Http3ResponseHead head) = await client.ConnectWebSocketAsync(
            new[] { ("sec-websocket-version", "13"), ("sec-websocket-protocol", "chat.v2, chat.v1") },
            cancellationToken);
        using WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });

        await socket.SendAsync(Encoding.UTF8.GetBytes("héllo over h3"), WebSocketMessageType.Text, true, cancellationToken);
        (WebSocketMessageType type, byte[] message) = await ReceiveMessageAsync(socket, cancellationToken);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);

        // Assert — RFC 9220 §3 / RFC 8441 §5: a 200 carrying the selected subprotocol, and neither the
        // HTTP/1.1 accept value nor its upgrade fields.
        head.StatusCode.ShouldBe(200);
        head.Field("sec-websocket-protocol").ShouldBe("chat.v2");
        head.Field("sec-websocket-accept").ShouldBeNull();
        head.Field("upgrade").ShouldBeNull();
        head.Field("connection").ShouldBeNull();
        type.ShouldBe(WebSocketMessageType.Text);
        Encoding.UTF8.GetString(message).ShouldBe("héllo over h3");
        socket.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        (await endpoint.Completed.WaitAsync(cancellationToken)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end/Http3: A cross-site handshake is refused with 403")]
    public async Task Http3_CrossSiteOrigin_Answers403()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(WebSocketTestProtocol.Http3, null, endpoint.InvokeAsync, cancellationToken);
        await using Http3TestClient client = await Http3TestClient.ConnectAsync(server.Http3Listener!, cancellationToken);

        // Act
        (Http3RequestStream stream, Http3ResponseHead head) = await client.ConnectWebSocketAsync(
            new[] { ("sec-websocket-version", "13"), ("origin", "https://attacker.example") },
            cancellationToken);
        await using Http3RequestStream _ = stream;

        // Assert
        head.StatusCode.ShouldBe(403);
        endpoint.Completed.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end/Http3: Another WebSocket version is refused with 426 and sec-websocket-version: 13, without upgrade or connection")]
    public async Task Http3_UnsupportedVersion_Answers426WithoutConnectionSpecificFields()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(WebSocketTestProtocol.Http3, null, endpoint.InvokeAsync, cancellationToken);
        await using Http3TestClient client = await Http3TestClient.ConnectAsync(server.Http3Listener!, cancellationToken);

        // Act
        (Http3RequestStream stream, Http3ResponseHead head) = await client.ConnectWebSocketAsync(
            new[] { ("sec-websocket-version", "8") },
            cancellationToken);
        await using Http3RequestStream _ = stream;

        // Assert — RFC 9114 §4.2: an HTTP/3 message never carries connection-specific fields.
        head.StatusCode.ShouldBe(426);
        head.Field("sec-websocket-version").ShouldBe("13");
        head.Field("upgrade").ShouldBeNull();
        head.Field("connection").ShouldBeNull();
        endpoint.Completed.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - End to end/Http3: Stopping the server closes an open socket with 1001 well before the budget runs out")]
    public async Task Http3_StopAsync_ClosesWith1001BeforeTheBudget()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        EchoEndpoint endpoint = new();
        await using WebSocketTestServer server = await WebSocketTestServer.StartAsync(WebSocketTestProtocol.Http3, null, endpoint.InvokeAsync, cancellationToken);
        await using Http3TestClient client = await Http3TestClient.ConnectAsync(server.Http3Listener!, cancellationToken);
        (Http3RequestStream stream, Http3ResponseHead head) = await client.ConnectWebSocketAsync(new[] { ("sec-websocket-version", "13") }, cancellationToken);
        head.StatusCode.ShouldBe(200);
        using WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
        await socket.SendAsync(Encoding.UTF8.GetBytes("before the stop"), WebSocketMessageType.Text, true, cancellationToken);
        await ReceiveMessageAsync(socket, cancellationToken);

        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(20));
        Stopwatch elapsed = Stopwatch.StartNew();

        // Act — the drain sends 1001 on the request stream; the client answers it.
        Task stop = server.Server.StopAsync(budget.Token);
        WebSocketReceiveResult close = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), cancellationToken);
        await socket.CloseOutputAsync(close.CloseStatus!.Value, close.CloseStatusDescription, cancellationToken);
        await stop.WaitAsync(cancellationToken);

        // Assert
        close.MessageType.ShouldBe(WebSocketMessageType.Close);
        close.CloseStatus.ShouldBe(WebSocketCloseStatus.EndpointUnavailable);
        budget.IsCancellationRequested.ShouldBeFalse();
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        (await endpoint.Completed.WaitAsync(cancellationToken)).ShouldBeNull();
    }

    private static ClientWebSocket CreateHttp2Client()
    {
        ClientWebSocket client = new();
        client.Options.HttpVersion = NetHttpVersion.Version20;
        client.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        client.Options.CollectHttpResponseDetails = true;
        return client;
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
}
