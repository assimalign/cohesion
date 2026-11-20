using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// The application's <c>forward-proxy</c> setting: the rule list, which an operator surface may replace
/// while the application runs, and the connection pools, forwarders and tunnels the rules lead to.
/// </summary>
/// <remarks>
/// Destinations an exact pattern allows and destinations allowed only through a wildcard pattern use
/// separate pools, so a connection opened for one is never reused for the other, even after the rules
/// change. The restricted pool and tunnel connect through <see cref="ForwardProxyAddressPolicy"/>.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("{Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ForwardProxySetting : IWebApplicationSetting<IReadOnlyList<ForwardProxyRule>>, IDisposable
{
    /// <summary>
    /// The setting's name.
    /// </summary>
    public const string SettingName = "forward-proxy";

    private readonly HttpMessageInvoker _openInvoker;
    private readonly HttpMessageInvoker _restrictedInvoker;
    private readonly HttpForwarder _openForwarder;
    private readonly HttpForwarder _restrictedForwarder;
    private readonly HttpTunnel _openTunnel;
    private readonly HttpTunnel _restrictedTunnel;
    private ForwardProxyRuleTable _table;

    private ForwardProxySetting(
        HttpMessageInvoker openInvoker,
        HttpMessageInvoker restrictedInvoker,
        HttpForwarderOptions forwarder,
        HttpTunnelOptions tunnel,
        ForwardProxyRuleTable table)
    {
        _openInvoker = openInvoker;
        _restrictedInvoker = restrictedInvoker;

        // Both forwarders are created from the one options instance, so they share its Via pseudonym.
        _openForwarder = HttpForwarder.Create(openInvoker, forwarder);
        _restrictedForwarder = HttpForwarder.Create(restrictedInvoker, forwarder);

        _openTunnel = HttpTunnel.Create(tunnel);
        _restrictedTunnel = HttpTunnel.Create(new HttpTunnelOptions
        {
            ConnectTimeout = tunnel.ConnectTimeout,
            IdleTimeout = tunnel.IdleTimeout,
            ConnectCallback = ForwardProxyAddressPolicy.ConnectAsync,
        });

        _table = table;
    }

    public string Name => SettingName;

    public IReadOnlyList<ForwardProxyRule> Value => Table.Rules;

    /// <summary>
    /// Gets the compiled rules in effect.
    /// </summary>
    public ForwardProxyRuleTable Table => Volatile.Read(ref _table);

    public void Set(IReadOnlyList<ForwardProxyRule> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Validate and compile the whole value before anything changes: a request sees the old rules or the
        // new ones, never a mix.
        ForwardProxyRuleTable table = ForwardProxyRuleTable.Create(value, nameof(value));
        Volatile.Write(ref _table, table);
    }

    /// <summary>
    /// Gets the forwarder for what the rules allowed.
    /// </summary>
    public HttpForwarder GetForwarder(ForwardProxyAccess access)
    {
        return access == ForwardProxyAccess.Open ? _openForwarder : _restrictedForwarder;
    }

    /// <summary>
    /// Gets the tunnel for what the rules allowed.
    /// </summary>
    public HttpTunnel GetTunnel(ForwardProxyAccess access)
    {
        return access == ForwardProxyAccess.Open ? _openTunnel : _restrictedTunnel;
    }

    public void Dispose()
    {
        _openInvoker.Dispose();
        _restrictedInvoker.Dispose();
    }

    /// <summary>
    /// Creates the setting and the connection pools it owns from a snapshot of the options.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The handler callback left a handler unable to proxy or connecting on its own, or the tunnel options
    /// name a connect callback.
    /// </exception>
    public static ForwardProxySetting Create(ForwardProxyOptions options)
    {
        if (options.Tunnel.ConnectCallback is not null)
        {
            throw new InvalidOperationException(ErrorMessages.ForwardProxyTunnelConnects);
        }

        HttpMessageInvoker openInvoker = ProxyHttpHandler.CreateInvoker(options.ConfigureHandler, forwardProxy: true, connectCallback: null);
        HttpMessageInvoker? restrictedInvoker = null;
        ForwardProxySetting? setting = null;
        try
        {
            restrictedInvoker = ProxyHttpHandler.CreateInvoker(options.ConfigureHandler, forwardProxy: true, ForwardProxyAddressPolicy.ConnectAsync);
            setting = new ForwardProxySetting(
                openInvoker,
                restrictedInvoker,
                options.Forwarder,
                options.Tunnel,
                ForwardProxyRuleTable.Create(options.Entries));
            return setting;
        }
        finally
        {
            // Nothing owns the pools until the setting exists.
            if (setting is null)
            {
                restrictedInvoker?.Dispose();
                openInvoker.Dispose();
            }
        }
    }

    /// <summary>
    /// Finds the setting <c>AddForwardProxy</c> registered last, or <see langword="null"/> when it was not
    /// called.
    /// </summary>
    public static ForwardProxySetting? Find(IWebApplicationContext context)
    {
        return context.Settings.OfType<ForwardProxySetting>().LastOrDefault();
    }

    private sealed class DebugView
    {
        private readonly ForwardProxySetting _target;

        public DebugView(ForwardProxySetting value)
        {
            _target = value;
        }

        public string Name => _target.Name;
        public IReadOnlyList<ForwardProxyRule> Value => _target.Value;
    }
}
