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
[global::System.Diagnostics.DebuggerDisplay("WebSocketPolicyFeature {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class WebSocketPolicyFeature : IHttpWebSocketFeature
{
    private readonly IHttpContext _context;
    private readonly IHttpWebSocketFeature _inner;
    private readonly WebSocketPolicy _policy;
    private readonly IWebServerDrainFeature? _drain;
    private WebSocket? _accepted;

    public WebSocketPolicyFeature(IHttpContext context, IHttpWebSocketFeature inner, WebSocketPolicy policy, IWebServerDrainFeature? drain)
    {
        _context = context;
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

        // Lifetime accounting belongs to the exchange even when a custom server has no drain
        // signal. A failed accept never reaches this transition.
        if (_inner is not WebSocketPolicyFeature)
        {
            WebSocketEventSource.Log.WebSocketOpened(_context, socket);
        }
        // The inner policy already attached the drain. Wrapping twice could misattribute the
        // outer drain's close as an application close at the inner lifetime owner.
        _accepted = _inner is WebSocketPolicyFeature || _drain is null
            ? socket
            : new DrainAwareWebSocket(_context, socket, _drain.Draining);
        return _accepted;
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
        WebSocket? accepted = Interlocked.Exchange(ref _accepted, null);
        if (accepted is null)
        {
            return;
        }

        bool closedByDrain = false;
        if (accepted is DrainAwareWebSocket drainAware)
        {
            drainAware.ReleaseDrain();
            closedByDrain = drainAware.ClosedByDrain;
        }

        if (_inner is not WebSocketPolicyFeature)
        {
            WebSocketEventSource.Log.WebSocketClosed(_context, accepted, closedByDrain);
        }
    }

    private sealed class DebugView
    {
        private readonly WebSocketPolicyFeature _target;

        public DebugView(WebSocketPolicyFeature value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public HttpWebSocketHandshakeStatus HandshakeStatus => _target.HandshakeStatus;
        public bool IsWebSocketRequest => _target.IsWebSocketRequest;
        public IReadOnlyList<string> RequestedProtocols => _target.RequestedProtocols;
    }
}
