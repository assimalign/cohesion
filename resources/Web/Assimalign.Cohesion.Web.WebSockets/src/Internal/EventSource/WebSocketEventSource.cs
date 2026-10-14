using System;
using System.Diagnostics.Tracing;
using System.Net.WebSockets;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.WebSockets.Internal;

[EventSource(Name = "Assimalign.Cohesion.Web.WebSockets")]
internal sealed class WebSocketEventSource : EventSource
{
    public static readonly WebSocketEventSource Log = new();

    private long _currentWebSockets;
    private long _totalWebSockets;
    private long _rejectedHandshakes;
    private PollingCounter? _currentCounter;
    private PollingCounter? _totalCounter;
    private PollingCounter? _rejectedCounter;

    private WebSocketEventSource() { }

    internal long CurrentWebSockets => Interlocked.Read(ref _currentWebSockets);

    internal long TotalWebSockets => Interlocked.Read(ref _totalWebSockets);

    internal long RejectedHandshakes => Interlocked.Read(ref _rejectedHandshakes);

    [NonEvent]
    public void HandshakeRejected(IHttpContext context, HttpWebSocketHandshakeStatus status)
    {
        Interlocked.Increment(ref _rejectedHandshakes);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            HandshakeRejected(status.ToString(), string.Empty, GetRouteTemplate(context), GetRequestId(context));
        }
    }

    [NonEvent]
    public void HandshakeRejected(IHttpContext context, string reason, string? normalizedOrigin)
    {
        Interlocked.Increment(ref _rejectedHandshakes);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            string origin = reason == "OriginNotAllowed" ? normalizedOrigin ?? string.Empty : string.Empty;
            HandshakeRejected(reason, origin.Length > 256 ? origin[..256] : origin, GetRouteTemplate(context), GetRequestId(context));
        }
    }

    [NonEvent]
    public void WebSocketOpened(IHttpContext context, WebSocket socket)
    {
        Interlocked.Increment(ref _currentWebSockets);
        Interlocked.Increment(ref _totalWebSockets);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            // The application selected this protocol from the offer; bound it even when an
            // application chooses one dynamically from untrusted input.
            string subProtocol = socket.SubProtocol ?? string.Empty;
            WebSocketOpened(GetRequestId(context), subProtocol.Length > 128 ? subProtocol[..128] : subProtocol);
        }
    }

    [NonEvent]
    public void WebSocketClosed(IHttpContext context, WebSocket socket, bool closedByDrain)
    {
        Interlocked.Decrement(ref _currentWebSockets);
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            WebSocketClosed(GetRequestId(context), socket.CloseStatus is { } status ? (int)status : -1, closedByDrain);
        }
    }

    [NonEvent]
    public void DrainCloseFailed(IHttpContext context, Exception exception)
    {
        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            DrainCloseFailed(GetRequestId(context), exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "WebSocket handshake refused ({0}) for origin {1}, route {2}, request {3}")]
    private void HandshakeRejected(string reason, string origin, string routeTemplate, string requestId)
        => WriteEvent(1, reason, origin, routeTemplate, requestId);

    [Event(2, Level = EventLevel.Verbose, Message = "WebSocket opened for request {0}, subprotocol {1}")]
    private void WebSocketOpened(string requestId, string subProtocol)
        => WriteEvent(2, requestId, subProtocol);

    [Event(3, Level = EventLevel.Verbose, Message = "WebSocket closed for request {0}, status {1}, drain {2}")]
    private void WebSocketClosed(string requestId, int closeStatus, bool closedByDrain)
        => WriteEvent(3, requestId, closeStatus, closedByDrain);

    [Event(4, Level = EventLevel.Verbose, Message = "WebSocket drain close failed for request {0} ({1}): {2}")]
    private void DrainCloseFailed(string requestId, string exceptionType, string exceptionMessage)
        => WriteEvent(4, requestId, exceptionType, exceptionMessage);

    [NonEvent]
    private static string GetRequestId(IHttpContext context)
        => context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty;

    [NonEvent]
    private static string GetRouteTemplate(IHttpContext context)
        => context.Features.Get<IWebEndpointFeature>()?.RouteTemplate ?? string.Empty;

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _currentCounter ??= new PollingCounter("current-websockets", this, () => CurrentWebSockets)
            {
                DisplayName = "Current WebSockets",
            };
            _totalCounter ??= new PollingCounter("total-websockets", this, () => TotalWebSockets)
            {
                DisplayName = "Total WebSockets",
            };
            _rejectedCounter ??= new PollingCounter("rejected-handshakes", this, () => RejectedHandshakes)
            {
                DisplayName = "Rejected WebSocket handshakes",
            };
        }
    }
}
