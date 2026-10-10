using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;
using NetHttpVersion = System.Net.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// The HTTP/2 extended CONNECT tunnel end to end (RFC 8441, #1316): a real .NET
/// <see cref="ClientWebSocket"/> opens a WebSocket over prior-knowledge HTTP/2 through
/// <see cref="WebApplicationTestFactory"/>, the application accepts the extended CONNECT and runs the
/// BCL's server-side WebSocket over the tunnel, and the server's finalization ends the tunnel instead of
/// writing the buffered response.
/// </summary>
public class WebApplicationServerExtendedConnectTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server/Http2: A WebSocket over an extended CONNECT tunnel should echo messages and close cleanly")]
    public async Task Http2_WebSocketOverExtendedConnect_ShouldEchoMessagesAndCloseCleanly()
    {
        // Arrange — the application accepts a websocket extended CONNECT and echoes every message until
        // the client closes.
        using CancellationTokenSource cancellation = new(_testTimeout);
        CancellationToken cancellationToken = cancellation.Token;
        TaskCompletionSource<string?> serverClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using WebApplicationTestFactory factory = new(new WebApplicationTestFactoryOptions
        {
            Protocol = WebApplicationTestProtocol.Http2,
        });

        factory.Application.Use(async (context, next) =>
        {
            if (context.Features.Get<IHttpExtendedConnectFeature>() is not { Protocol: "websocket" } extendedConnect)
            {
                context.Response.StatusCode = CohesionHttpStatusCode.BadRequest;
                return;
            }

            await using Stream tunnel = await extendedConnect.AcceptAsync(cancellationToken);
            using WebSocket socket = WebSocket.CreateFromStream(tunnel, new WebSocketCreationOptions
            {
                IsServer = true,
                KeepAliveInterval = TimeSpan.Zero,
            });

            byte[] buffer = new byte[1024];

            while (true)
            {
                ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
                    serverClosed.TrySetResult(socket.CloseStatusDescription);
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, cancellationToken);
            }
        });

        using HttpClient client = factory.CreateClient();
        using ClientWebSocket webSocket = new();
        webSocket.Options.HttpVersion = NetHttpVersion.Version20;
        webSocket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        webSocket.Options.KeepAliveInterval = TimeSpan.Zero;
        webSocket.Options.CollectHttpResponseDetails = true;

        // Act — the handshake is the extended CONNECT; every message then crosses the tunnel both ways.
        await webSocket.ConnectAsync(new Uri("ws://localhost/chat"), client, cancellationToken);

        string[] echoed = new string[3];
        string[] messages = { "hello", "over", "h2" };

        for (int index = 0; index < messages.Length; index++)
        {
            await webSocket.SendAsync(Encoding.UTF8.GetBytes(messages[index]), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

            byte[] buffer = new byte[64];
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            echoed[index] = Encoding.UTF8.GetString(buffer, 0, result.Count);
        }

        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken);

        // Assert — a 200 over HTTP/2 established the socket, the echo arrived intact, and both sides
        // completed the close handshake.
        webSocket.HttpStatusCode.ShouldBe(NetHttpStatusCode.OK);
        echoed.ShouldBe(messages);
        webSocket.State.ShouldBe(WebSocketState.Closed);
        webSocket.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        (await serverClosed.Task.WaitAsync(cancellationToken)).ShouldBe("bye");
    }
}
