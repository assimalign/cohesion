using System;
using System.Collections.Generic;
using System.Net.Http;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Proxy.Internal;

namespace Assimalign.Cohesion.Web.Proxy;

/// <summary>
/// The forward proxy <c>builder.Services.AddForwardProxy(...)</c> registers
/// (<see cref="ForwardProxyBuilder.Options"/>): its rules, how it forwards and tunnels, and its
/// connection pools.
/// </summary>
/// <remarks>
/// <para>
/// Each rule is validated when it is added. The registered setting takes a copy of everything here
/// when <c>AddForwardProxy</c> runs, so changing the options afterwards has no effect. Only the rules
/// can change later, as a whole, through the setting.
/// </para>
/// <para>
/// The proxy keeps two connection pools, one for destinations an exact pattern allows and one for
/// destinations allowed only through a wildcard pattern, whose connect step refuses loopback,
/// link-local and unspecified addresses. Each is a <see cref="SocketsHttpHandler"/> created with
/// proxying defaults: no proxy of its own, no automatic redirects, no cookie container, no automatic
/// decompression, no <c>traceparent</c> of its own, and a 15-second connect timeout.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("ForwardProxyOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class ForwardProxyOptions
{
    private readonly List<ForwardProxyRuleEntry> _entries = new();
    private readonly List<ForwardProxyRule> _rules = new();

    /// <summary>
    /// Gets the rules added so far, in the order they were added, with copies of their lists.
    /// </summary>
    public IReadOnlyList<ForwardProxyRule> Rules => _rules.AsReadOnly();

    /// <summary>
    /// Gets how the proxy forwards an absolute-form request: the forwarded headers it adds, its
    /// <c>Via</c> pseudonym, its activity timeout, the HTTP version it asks for, and the largest
    /// response it buffers when it cannot stream.
    /// </summary>
    /// <remarks>
    /// The defaults differ from a reverse proxy's in two places.
    /// <see cref="HttpForwarderOptions.ForwardedHeaderAction"/> is
    /// <see cref="HttpForwardedHeaderAction.None"/>, so the proxy does not tell the sites its clients
    /// visit who those clients are. <see cref="HttpForwarderOptions.AddViaToResponses"/> is
    /// <see langword="true"/>, because a proxy adds <c>Via</c> to the responses it forwards as well as to
    /// the requests (RFC 9110 §7.6.3).
    /// </remarks>
    public HttpForwarderOptions Forwarder { get; } = new()
    {
        ForwardedHeaderAction = HttpForwardedHeaderAction.None,
        AddViaToResponses = true,
    };

    /// <summary>
    /// Gets how the proxy serves a <c>CONNECT</c>: how long it waits to connect, and how long an open
    /// tunnel may stay idle.
    /// </summary>
    /// <remarks>
    /// <see cref="HttpTunnelOptions.ConnectCallback"/> must stay <see langword="null"/>: the proxy
    /// connects each tunnel itself, so it can check the address it connects to.
    /// </remarks>
    public HttpTunnelOptions Tunnel { get; } = new();

    /// <summary>
    /// Gets or sets a callback that adjusts each of the proxy's <see cref="SocketsHttpHandler"/>
    /// instances after their proxying defaults are applied, or <see langword="null"/> to keep them.
    /// </summary>
    /// <remarks>
    /// The callback runs once for each connection pool while <c>AddForwardProxy</c> runs. Registration
    /// fails when it leaves <see cref="SocketsHttpHandler.AllowAutoRedirect"/> or
    /// <see cref="SocketsHttpHandler.UseCookies"/> on, or <see cref="SocketsHttpHandler.AutomaticDecompression"/>
    /// other than <see cref="System.Net.DecompressionMethods.None"/>, because the proxy would change what it
    /// relays; and when it sets <see cref="SocketsHttpHandler.ConnectCallback"/> or turns
    /// <see cref="SocketsHttpHandler.UseProxy"/> on, because the proxy connects to each destination
    /// itself, so it can check the address it connects to.
    /// </remarks>
    public Action<SocketsHttpHandler>? ConfigureHandler { get; set; }

    /// <summary>
    /// Adds a rule.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rule"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The rule names no host, has a host pattern that is not valid, or has a port outside 1 to 65535.
    /// </exception>
    public ForwardProxyOptions AddRule(ForwardProxyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        ForwardProxyRuleEntry entry = ForwardProxyRuleEntry.Create(rule, _entries.Count, nameof(rule));
        _entries.Add(entry);
        _rules.Add(entry.Rule);
        return this;
    }

    /// <summary>
    /// Gets the compiled rules the setting starts with.
    /// </summary>
    internal IReadOnlyList<ForwardProxyRuleEntry> Entries => _entries;

    private sealed class DebugView
    {
        private readonly ForwardProxyOptions _target;

        public DebugView(ForwardProxyOptions value)
        {
            _target = value;
        }

        public IReadOnlyList<ForwardProxyRule> Rules => _target.Rules;
        public HttpForwarderOptions Forwarder => _target.Forwarder;
        public HttpTunnelOptions Tunnel => _target.Tunnel;
        public Action<SocketsHttpHandler>? ConfigureHandler => _target.ConfigureHandler;
    }
}
