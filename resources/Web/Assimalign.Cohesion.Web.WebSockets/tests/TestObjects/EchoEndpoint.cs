using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// A WebSocket echo endpoint written the way an application writes one: accept, echo every frame
/// until the peer's close, then complete the close handshake with <c>CloseAsync</c>. It records how
/// it ended, so a test can tell a clean close from a protocol failure.
/// </summary>
internal sealed class EchoEndpoint
{
    private readonly TaskCompletionSource<Exception?> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets or sets the accept options; <see langword="null"/> accepts with the policy's defaults.</summary>
    public HttpWebSocketAcceptOptions? AcceptOptions { get; set; }

    /// <summary>Gets or sets whether the first offered subprotocol is selected.</summary>
    public bool SelectFirstSubProtocol { get; set; }

    /// <summary>
    /// Completes when the endpoint returns: with <see langword="null"/> after a clean close, or with
    /// the exception that ended it.
    /// </summary>
    public Task<Exception?> Completed => _completed.Task;

    /// <summary>Gets the subprotocols the client offered, as the endpoint saw them.</summary>
    public IReadOnlyList<string>? RequestedProtocols { get; private set; }

    public async Task InvokeAsync(IHttpContext context)
    {
        IHttpWebSocketFeature webSockets = context.WebSockets;

        if (!webSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            _completed.TrySetResult(null);
            return;
        }

        try
        {
            RequestedProtocols = webSockets.RequestedProtocols;

            HttpWebSocketAcceptOptions? options = AcceptOptions;
            if (SelectFirstSubProtocol && webSockets.RequestedProtocols.Count > 0)
            {
                options = new HttpWebSocketAcceptOptions { SubProtocol = webSockets.RequestedProtocols[0] };
            }

            using WebSocket socket = await webSockets.AcceptWebSocketAsync(options, context.RequestCancelled);
            byte[] buffer = new byte[4096];

            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestCancelled);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription, CancellationToken.None);
                    break;
                }

                await socket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, context.RequestCancelled);
            }

            _completed.TrySetResult(null);
        }
        catch (Exception exception)
        {
            _completed.TrySetResult(exception);
            throw;
        }
    }
}
