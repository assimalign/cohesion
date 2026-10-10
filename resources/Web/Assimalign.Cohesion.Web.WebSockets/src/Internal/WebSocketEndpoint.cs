using System;
using System.Net.WebSockets;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Internal;

/// <summary>
/// The terminal a <c>MapWebSocket</c> route runs: it answers a request that is not a WebSocket
/// handshake with <c>400</c>, guards the handshake with the WebSocket policy, accepts it, and runs
/// the application's handler over the accepted socket.
/// </summary>
/// <remarks>
/// <para>
/// The route matches <c>GET</c> (the HTTP/1.1 upgrade, RFC 6455) and <c>CONNECT</c> (the HTTP/2 and
/// HTTP/3 extended CONNECT, RFC 8441 and RFC 9220); <c>Http.WebSockets</c> recognizes either
/// handshake, so the handler is the same on every protocol.
/// </para>
/// <para>
/// When <c>UseWebSockets</c> ran ahead of the endpoint, it already refused a malformed or cross-site
/// handshake and installed its policy decorator. Otherwise the endpoint applies the default policy
/// itself, exactly as <c>UseWebSockets()</c> with default options would: an endpoint mapped for
/// sockets is never left without the cross-site hijacking defense.
/// </para>
/// </remarks>
internal sealed class WebSocketEndpoint
{
    private static readonly WebSocketMiddleware _defaultPolicy = new(WebSocketPolicy.Create(new WebSocketOptions()));

    private readonly Func<IHttpContext, WebSocket, Task> _handler;
    private readonly Func<IHttpContext, HttpWebSocketAcceptOptions?>? _acceptOptions;
    private readonly WebApplicationMiddleware _acceptAndRun;

    public WebSocketEndpoint(Func<IHttpContext, WebSocket, Task> handler, Func<IHttpContext, HttpWebSocketAcceptOptions?>? acceptOptions)
    {
        _handler = handler;
        _acceptOptions = acceptOptions;
        _acceptAndRun = AcceptAndRunAsync;
    }

    /// <summary>Gets the methods the endpoint's route matches: the two handshake shapes.</summary>
    public static HttpMethod[] Methods { get; } = [HttpMethod.Get, HttpMethod.Connect];

    /// <summary>Serves a request the endpoint's route matched.</summary>
    /// <param name="context">The exchange.</param>
    /// <returns>A task that completes when the socket's handler has returned.</returns>
    public Task InvokeAsync(IHttpContext context)
    {
        IHttpWebSocketFeature webSockets = context.WebSockets;

        // A request to a socket endpoint that is not a handshake at all (a plain GET or HEAD, a
        // CONNECT without :protocol) is the client's error, on every protocol: 400, and the handler
        // never runs.
        if (webSockets.HandshakeStatus == HttpWebSocketHandshakeStatus.None)
        {
            context.Response.StatusCode = HttpStatusCode.BadRequest;
            return Task.CompletedTask;
        }

        // UseWebSockets guarded this exchange already: its decorator is the installed feature.
        if (webSockets is WebSocketPolicyFeature)
        {
            return _acceptAndRun(context);
        }

        return _defaultPolicy.InvokeAsync(context, _acceptAndRun);
    }

    private async Task AcceptAndRunAsync(IHttpContext context)
    {
        // The policy's decorator, so the accept takes its defaults and the drain close.
        IHttpWebSocketFeature webSockets = context.WebSockets;
        HttpWebSocketAcceptOptions? options = _acceptOptions?.Invoke(context);

        using WebSocket socket = await webSockets.AcceptWebSocketAsync(options, context.RequestCancelled).ConfigureAwait(false);
        await _handler(context, socket).ConfigureAwait(false);
    }
}
