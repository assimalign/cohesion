using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.WebSockets.Internal;

/// <summary>
/// The <see cref="IHttpWebSocketFeature"/> <c>UseWebSockets</c> installs over the exchange's own:
/// every accept takes the policy's keep-alive and compression defaults, and, when the server
/// publishes its drain signal, the accepted socket closes with <c>1001</c> when the drain begins.
/// </summary>
/// <remarks>
/// The decorator takes the inner feature's <see cref="Name"/>, so installing it replaces the inner
/// feature in the exchange's features: <c>context.WebSockets</c> then reads the decorator, and the
/// handshake, the subprotocols and the single-shot accept stay the inner feature's.
/// </remarks>
internal sealed class WebSocketPolicyFeature : IHttpWebSocketFeature
{
    private readonly IHttpWebSocketFeature _inner;
    private readonly WebSocketPolicy _policy;
    private readonly IWebServerDrainFeature? _drain;
    private DrainAwareWebSocket? _accepted;

    public WebSocketPolicyFeature(IHttpWebSocketFeature inner, WebSocketPolicy policy, IWebServerDrainFeature? drain)
    {
        _inner = inner;
        _policy = policy;
        _drain = drain;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public HttpWebSocketHandshakeStatus HandshakeStatus => _inner.HandshakeStatus;

    /// <inheritdoc />
    public bool IsWebSocketRequest => _inner.IsWebSocketRequest;

    /// <inheritdoc />
    public IReadOnlyList<string> RequestedProtocols => _inner.RequestedProtocols;

    /// <inheritdoc />
    public async ValueTask<WebSocket> AcceptWebSocketAsync(
        HttpWebSocketAcceptOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        WebSocket socket = await _inner.AcceptWebSocketAsync(_policy.Apply(options), cancellationToken).ConfigureAwait(false);

        // A custom server may not publish a drain signal; its sockets then end when it ends them.
        if (_drain is null)
        {
            return socket;
        }

        DrainAwareWebSocket drainAware = new(socket, _drain.Draining);
        _accepted = drainAware;

        return drainAware;
    }

    /// <inheritdoc />
    public void RejectHandshake()
    {
        _inner.RejectHandshake();
    }

    /// <summary>
    /// Stops watching the drain for the accepted socket, if any. Called when the rest of the
    /// pipeline returns, which ends the exchange and its connection.
    /// </summary>
    public void Release()
    {
        _accepted?.ReleaseDrain();
    }
}
