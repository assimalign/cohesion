using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http.Connections;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.WebSockets.Tests;

/// <summary>
/// The package on its own, without the Web host: a bare HTTP/1.1 listener with the
/// protocol-upgrade interceptor serves a WebSocket to the BCL client over loopback TCP.
/// </summary>
public class HttpWebSocketTransportTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Http.WebSockets] - Transport: A bare HTTP/1.1 listener accepts a WebSocket and echoes a message")]
    public async Task AcceptWebSocketAsync_BareHttp1Listener_EchoesAMessage()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;

        TcpConnectionListener tcp = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using HttpConnectionListener listener = HttpConnectionListener.Create(options =>
        {
            options.UseHttp1(tcp);
            options.Interceptors.Add(HttpProtocolUpgrade.CreateInterceptor());
        });
        await listener.BindAsync(cancellationToken);
        int port = ((IPEndPoint)tcp.EndPoint).Port;

        Task<string?> server = ServeOneSocketAsync(listener, cancellationToken);

        using ClientWebSocket client = new();
        client.Options.AddSubProtocol("echo");

        // Act
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/echo"), cancellationToken);
        await client.SendAsync(Encoding.UTF8.GetBytes("hello over a bare listener"), WebSocketMessageType.Text, true, cancellationToken);

        byte[] buffer = new byte[256];
        ValueWebSocketReceiveResult echoed = await client.ReceiveAsync(buffer.AsMemory(), cancellationToken);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);

        // Assert
        client.SubProtocol.ShouldBe("echo");
        Encoding.UTF8.GetString(buffer, 0, echoed.Count).ShouldBe("hello over a bare listener");
        client.CloseStatus.ShouldBe(WebSocketCloseStatus.NormalClosure);
        (await server).ShouldBe("hello over a bare listener");
    }

    /// <summary>
    /// Serves one connection the way a host does: receive the exchange, run the handler, then send
    /// (a no-op once the connection was taken over) and dispose it.
    /// </summary>
    private static async Task<string?> ServeOneSocketAsync(HttpConnectionListener listener, CancellationToken cancellationToken)
    {
        await using IHttpConnection connection = await listener.AcceptOrListenAsync(cancellationToken);
        IHttpConnectionContext connectionContext = await connection.OpenAsync(cancellationToken);
        string? message = null;

        await foreach (IHttpContext context in connectionContext.ReceiveAsync(cancellationToken))
        {
            IHttpWebSocketFeature webSockets = context.WebSockets;

            if (webSockets.IsWebSocketRequest)
            {
                using WebSocket socket = await webSockets.AcceptWebSocketAsync(
                    new HttpWebSocketAcceptOptions { SubProtocol = webSockets.RequestedProtocols[0] },
                    cancellationToken);

                byte[] buffer = new byte[256];
                ValueWebSocketReceiveResult received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                message = Encoding.UTF8.GetString(buffer, 0, received.Count);
                await socket.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Text, true, cancellationToken);

                ValueWebSocketReceiveResult close = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (close.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure, socket.CloseStatusDescription, cancellationToken);
                }
            }

            await connectionContext.SendAsync(context, cancellationToken);
            await context.DisposeAsync();
        }

        return message;
    }
}
