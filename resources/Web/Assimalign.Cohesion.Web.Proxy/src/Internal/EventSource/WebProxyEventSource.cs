using System;
using System.Diagnostics.Tracing;
using System.Net;
using System.Threading;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// The forward proxy's refusals: a request its rules or its address policy refuse, and a connection
/// its address policy refuses after resolving the destination. How a forwarded exchange or a tunnel
/// ends, failures included, is Http.Proxy's to report, so the reverse proxy raises no event of its own.
/// </summary>
/// <remarks>
/// The destination host is the diagnosis of a refusal, so it is written, at Verbose only, truncated to
/// 253 characters inside the enabled check and never accompanied by the request target's path or query.
/// </remarks>
[EventSource(Name = "Assimalign.Cohesion.Web.Proxy")]
internal sealed class WebProxyEventSource : EventSource
{
    private const int maxHostLength = 253;

    public static readonly WebProxyEventSource Log = new();

    private PollingCounter? _refusedRequestsCounter;
    private PollingCounter? _refusedConnectionsCounter;
    private long _refusedRequests;
    private long _refusedConnections;

    private WebProxyEventSource()
    {
    }

    internal long RefusedRequests => Interlocked.Read(ref _refusedRequests);

    internal long RefusedConnections => Interlocked.Read(ref _refusedConnections);

    /// <summary>
    /// Reports a request the forward proxy answered with 403 Forbidden: no rule allows its host and
    /// port, or (<paramref name="addressRefused"/>) its host is an IP literal the address policy
    /// refuses and only a wildcard pattern allows it.
    /// </summary>
    [NonEvent]
    public void RequestRefused(IHttpContext context, string host, int port, bool addressRefused)
    {
        Interlocked.Increment(ref _refusedRequests);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            RequestRefused(
                addressRefused ? "AddressRefused" : "NotAllowed",
                Bound(host),
                port,
                context.Features.Get<IWebRequestIdFeature>()?.RequestId.ToHexString() ?? string.Empty);
        }
    }

    [Event(1, Level = EventLevel.Verbose, Message = "Forward proxy refused {1}:{2} ({0}); request {3}")]
    private void RequestRefused(string reason, string host, int port, string requestId)
        => WriteEvent(1, reason, host, port, requestId);

    /// <summary>
    /// Reports a connection the address policy refused: the destination, reached only through a
    /// wildcard pattern, resolved to <paramref name="addressCount"/> addresses and the policy refuses
    /// every one of them. The forwarder or the tunnel then answers 502 Bad Gateway.
    /// </summary>
    [NonEvent]
    public void ConnectionRefused(DnsEndPoint destination, int addressCount)
    {
        Interlocked.Increment(ref _refusedConnections);

        if (IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            ConnectionRefused(Bound(destination.Host), destination.Port, addressCount);
        }
    }

    [Event(2, Level = EventLevel.Verbose, Message = "Forward proxy refused all {2} addresses of {0}; port {1}")]
    private void ConnectionRefused(string host, int port, int addressCount)
        => WriteEvent(2, host, port, addressCount);

    protected override void OnEventCommand(EventCommandEventArgs command)
    {
        if (command.Command == EventCommand.Enable)
        {
            _refusedRequestsCounter ??= new PollingCounter("refused-requests", this, () => RefusedRequests)
            {
                DisplayName = "Refused forward-proxy requests",
            };
            _refusedConnectionsCounter ??= new PollingCounter("refused-connections", this, () => RefusedConnections)
            {
                DisplayName = "Refused forward-proxy connections",
            };
        }
    }

    private static string Bound(string host)
    {
        return host.Length > maxHostLength ? host[..maxHostLength] : host;
    }
}
