using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;

/// <summary>
/// Destination handlers for the end-to-end tests: one that describes the request it received, and one
/// that echoes WebSocket messages.
/// </summary>
internal static class ProxyTestBackend
{
    private static readonly string[] _reportedHeaders =
    [
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "Forwarded",
        "Via",
        "Proxy-Authorization",
        "Proxy-Connection",
    ];

    /// <summary>
    /// Answers 200 with one <c>name=value</c> line per property of the request the destination received:
    /// its method, path, query text, <c>Host</c>, the forwarding headers, and its body.
    /// </summary>
    public static async Task DescribeRequestAsync(IHttpContext context, string name)
    {
        IHttpRequest request = context.Request;
        string body;
        using (StreamReader reader = new(request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(context.RequestCancelled);
        }

        StringBuilder text = new();
        Append(text, "server", name);
        Append(text, "method", request.Method.ToString());
        Append(text, "path", request.Path.Value);
        Append(text, "query", request.Query is HttpQueryCollection { Source: HttpQuery source } ? source.Value : string.Empty);
        Append(text, "host", request.Host.Value);
        foreach (string header in _reportedHeaders)
        {
            Append(text, header.ToLowerInvariant(), request.Headers.TryGetValue(new HttpHeaderKey(header), out HttpHeaderValue value) ? value.ToString() : string.Empty);
        }

        Append(text, "body", body);

        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Headers["X-Backend"] = name;
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), context.RequestCancelled);
    }

    /// <summary>
    /// Reads the lines <see cref="DescribeRequestAsync"/> wrote.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string description)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in description.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=', StringComparison.Ordinal);
            values[line[..separator]] = line[(separator + 1)..];
        }

        return values;
    }

    /// <summary>
    /// Accepts a WebSocket and echoes each message until the client closes it; answers any other request
    /// with 200 and the text <c>not-a-websocket</c>.
    /// </summary>
    public static async Task EchoWebSocketAsync(IHttpContext context)
    {
        IHttpWebSocketFeature webSockets = context.WebSockets;
        if (!webSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = HttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("not-a-websocket"), context.RequestCancelled);
            return;
        }

        using WebSocket socket = await webSockets.AcceptWebSocketAsync(
            new HttpWebSocketAcceptOptions { KeepAliveInterval = TimeSpan.Zero },
            context.RequestCancelled);
        byte[] buffer = new byte[1024];

        while (true)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestCancelled);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            await socket.SendAsync(new ArraySegment<byte>(buffer, 0, received.Count), received.MessageType, received.EndOfMessage, context.RequestCancelled);
        }
    }

    private static void Append(StringBuilder text, string name, string? value)
    {
        text.Append(name).Append('=').Append(value).Append('\n');
    }
}
