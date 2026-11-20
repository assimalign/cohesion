using System;
using System.Net;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// Serves every request as a forward-proxy request: a <c>CONNECT</c> through a tunnel, any other method
/// through the forwarder, to the request's target when the <c>forward-proxy</c> setting's rules allow it.
/// The middleware is terminal; it never calls the next one.
/// </summary>
/// <remarks>
/// <para>
/// The target is the request's own host, which the transport takes from an absolute-form request target
/// or from a <c>CONNECT</c>'s authority, never the effective host a trusted proxy forwarded: forwarded
/// headers do not decide where a forward proxy connects. The transport does not yet say which form the
/// request target had (#1895), so an origin-form request is forwarded to its <c>Host</c> on the same
/// terms.
/// </para>
/// <para>
/// A malformed target, a <c>CONNECT</c> without a port, or a path whose dot segments hide behind an
/// encoded slash or a backslash is answered with 400 Bad Request. A target no rule allows, or an IP
/// literal the address policy refuses when only a wildcard pattern allows it, is answered with 403
/// Forbidden and reported as <c>RequestRefused</c>.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("ForwardProxyMiddleware")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ForwardProxyMiddleware : IWebApplicationMiddleware
{
    private const int httpPort = 80;
    private const int httpsPort = 443;

    private readonly ForwardProxySetting _setting;

    public ForwardProxyMiddleware(ForwardProxySetting setting)
    {
        _setting = setting;
    }

    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        IHttpRequest request = context.Request;
        HttpHost target = request.Host;

        if (!target.TryGetComponents(out ReadOnlySpan<char> hostComponent, out int? port) || hostComponent.IsEmpty)
        {
            return Answer(context, HttpStatusCode.BadRequest);
        }

        string host = hostComponent.ToString();
        UriHostNameType hostType = Uri.CheckHostName(host);
        if (hostType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return Answer(context, HttpStatusCode.BadRequest);
        }

        // An extended CONNECT carries a WebSocket, which the forwarder bridges; a plain one asks for a tunnel.
        bool tunnel = request.Method == HttpMethod.Connect && context.ExtendedConnect is null;
        if (tunnel && port is null)
        {
            // RFC 9110 §9.3.6: a CONNECT's request target is a host and a port.
            return Answer(context, HttpStatusCode.BadRequest);
        }

        bool https = !tunnel && request.Scheme == HttpScheme.Https;
        int defaultPort = tunnel || https ? httpsPort : httpPort;
        int targetPort = port ?? defaultPort;

        ForwardProxyAccess access = _setting.Table.Evaluate(target, targetPort, defaultPort);
        if (access == ForwardProxyAccess.Denied)
        {
            WebProxyEventSource.Log.RequestRefused(context, host, targetPort, addressRefused: false);
            return Answer(context, HttpStatusCode.Forbidden);
        }

        // The connect step refuses these addresses too; refusing a literal here answers 403, not 502.
        if (access == ForwardProxyAccess.Restricted
            && hostType != UriHostNameType.Dns
            && IPAddress.TryParse(host, out IPAddress? address)
            && ForwardProxyAddressPolicy.IsRefused(address))
        {
            WebProxyEventSource.Log.RequestRefused(context, host, targetPort, addressRefused: true);
            return Answer(context, HttpStatusCode.Forbidden);
        }

        if (tunnel)
        {
            return _setting.GetTunnel(access).RelayAsync(context, new DnsEndPoint(host, targetPort));
        }

        if (!HttpForwarder.TryNormalizePath(request.Path, out HttpPath path))
        {
            return Answer(context, HttpStatusCode.BadRequest);
        }

        Uri origin = new UriBuilder(https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp, host, port ?? -1).Uri;
        Uri destination = HttpForwarder.CreateDestination(origin, path, request.Query);

        // The forwarder answers the client itself, failures included, and reports how the exchange ended
        // through its own events.
        return _setting.GetForwarder(access).ForwardAsync(context, destination);
    }

    private static Task Answer(IHttpContext context, HttpStatusCode status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    private sealed class DebugView
    {
        public DebugView(ForwardProxyMiddleware value)
        {
        }
    }
}
