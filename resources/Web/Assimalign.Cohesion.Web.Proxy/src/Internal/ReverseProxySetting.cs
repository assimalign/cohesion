using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// The application's <c>reverse-proxy</c> setting: the route list, which an operator surface may
/// replace while the application runs, and the connection pool and forwarders every route shares.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("{Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ReverseProxySetting : IWebApplicationSetting<IReadOnlyList<ReverseProxyRoute>>, IDisposable
{
    /// <summary>
    /// The setting's name.
    /// </summary>
    public const string SettingName = "reverse-proxy";

    private readonly HttpMessageInvoker _invoker;
    private readonly HttpForwarder _forwarder;
    private readonly HttpForwarder _hostPreservingForwarder;
    private readonly bool _preserveHost;
    private ReverseProxyRouteTable _table;

    private ReverseProxySetting(
        HttpMessageInvoker invoker,
        HttpForwarder forwarder,
        HttpForwarder hostPreservingForwarder,
        bool preserveHost,
        ReverseProxyRouteTable table)
    {
        _invoker = invoker;
        _forwarder = forwarder;
        _hostPreservingForwarder = hostPreservingForwarder;
        _preserveHost = preserveHost;
        _table = table;
    }

    public string Name => SettingName;

    public IReadOnlyList<ReverseProxyRoute> Value => Table.Routes;

    /// <summary>
    /// Gets the compiled routes in effect.
    /// </summary>
    public ReverseProxyRouteTable Table => Volatile.Read(ref _table);

    public void Set(IReadOnlyList<ReverseProxyRoute> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Validate and compile the whole value before anything changes: a request sees the old routes or
        // the new ones, never a mix.
        ReverseProxyRouteTable table = ReverseProxyRouteTable.Create(value, nameof(value));
        Volatile.Write(ref _table, table);
    }

    /// <summary>
    /// Gets the forwarder for a route: the one that keeps the client's <c>Host</c> or the one that names
    /// the destination's authority.
    /// </summary>
    public HttpForwarder GetForwarder(ReverseProxyRouteEntry entry)
    {
        return (entry.Route.PreserveHost ?? _preserveHost) ? _hostPreservingForwarder : _forwarder;
    }

    public void Dispose()
    {
        _invoker.Dispose();
    }

    /// <summary>
    /// Creates the setting and the connection pool it owns from a snapshot of the options.
    /// </summary>
    /// <exception cref="InvalidOperationException">The handler callback left the handler unable to proxy.</exception>
    public static ReverseProxySetting Create(ReverseProxyOptions options)
    {
        HttpMessageInvoker invoker = ProxyHttpHandler.CreateInvoker(options.ConfigureHandler, forwardProxy: false, connectCallback: null);
        HttpForwarderOptions forwarder = options.Forwarder;
        bool preserveHost = forwarder.PreserveHost;

        // HttpForwarder.Create copies the options, so both forwarders are created from the one instance with
        // only PreserveHost changed between them, and it is put back after. They share every other value,
        // the Via pseudonym included, so a request that comes back through either one is a loop.
        ReverseProxySetting? setting = null;
        try
        {
            forwarder.PreserveHost = false;
            HttpForwarder destinationHost = HttpForwarder.Create(invoker, forwarder);
            forwarder.PreserveHost = true;
            HttpForwarder clientHost = HttpForwarder.Create(invoker, forwarder);

            setting = new ReverseProxySetting(invoker, destinationHost, clientHost, preserveHost, ReverseProxyRouteTable.Create(options.Entries));
            return setting;
        }
        finally
        {
            forwarder.PreserveHost = preserveHost;

            // Nothing owns the pool until the setting exists.
            if (setting is null)
            {
                invoker.Dispose();
            }
        }
    }

    /// <summary>
    /// Finds the setting <c>AddReverseProxy</c> registered last, or <see langword="null"/> when it was
    /// not called.
    /// </summary>
    public static ReverseProxySetting? Find(IWebApplicationContext context)
    {
        return context.Settings.OfType<ReverseProxySetting>().LastOrDefault();
    }

    private sealed class DebugView
    {
        private readonly ReverseProxySetting _target;

        public DebugView(ReverseProxySetting value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public IReadOnlyList<ReverseProxyRoute> Value => _target.Value;
    }
}
