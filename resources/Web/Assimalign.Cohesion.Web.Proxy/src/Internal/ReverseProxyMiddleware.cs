using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// Forwards each request a route of the <c>reverse-proxy</c> setting takes, and passes every other
/// request to the next middleware.
/// </summary>
/// <remarks>
/// The request is routed on its effective host and on its path with the dot segments removed, the
/// same path the destination receives. A path whose dot segments hide behind an encoded slash or a
/// backslash cannot be routed safely, since destinations read it differently: a route that would take
/// it answers 400 Bad Request, and any other such request goes to the next middleware, which never
/// forwards it.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("ReverseProxyMiddleware")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ReverseProxyMiddleware : IWebApplicationMiddleware
{
    private readonly ReverseProxySetting _setting;

    public ReverseProxyMiddleware(ReverseProxySetting setting)
    {
        _setting = setting;
    }

    public Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        // Read the routes once, so one request is routed against one value even if an operator replaces it.
        ReverseProxyRouteTable table = _setting.Table;
        if (table.IsEmpty)
        {
            return next.Invoke(context);
        }

        HttpHost host = context.EffectiveHost;
        HttpPath path = context.Request.Path;

        if (!HttpForwarder.TryNormalizePath(path, out HttpPath normalized))
        {
            if (table.Match(host, string.IsNullOrEmpty(path.Value) ? "/" : path.Value) is null)
            {
                return next.Invoke(context);
            }

            context.Response.StatusCode = HttpStatusCode.BadRequest;
            return Task.CompletedTask;
        }

        ReverseProxyRouteEntry? route = table.Match(host, normalized.Value);
        if (route is null)
        {
            return next.Invoke(context);
        }

        Uri destination = HttpForwarder.CreateDestination(
            route.NextDestination(),
            new HttpPath(route.GetForwardedPath(normalized.Value)),
            context.Request.Query);

        // The forwarder answers the client itself, failures included, and reports how the exchange ended
        // through its own events.
        return _setting.GetForwarder(route).ForwardAsync(context, destination);
    }

    private sealed class DebugView
    {
        public DebugView(ReverseProxyMiddleware value)
        {
        }
    }
}
