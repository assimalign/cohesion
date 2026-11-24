using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Proxy.Internal;

namespace Assimalign.Cohesion.Web.Proxy;

/// <summary>
/// The reverse proxy <c>builder.Services.AddReverseProxy(...)</c> registers
/// (<see cref="ReverseProxyBuilder.Options"/>): its routes, how it forwards, and its connection pool.
/// </summary>
/// <remarks>
/// <para>
/// Each route is validated when it is added. The registered setting takes a copy of everything here
/// when <c>AddReverseProxy</c> runs, so changing the options afterwards has no effect. Only the routes
/// can change later, as a whole, through the setting.
/// </para>
/// <para>
/// The proxy keeps one connection pool for all its routes, a <see cref="SocketsHttpHandler"/> it
/// creates with proxying defaults: no proxy of its own, no automatic redirects, no cookie container,
/// no automatic decompression, no <c>traceparent</c> of its own, and a 15-second connect timeout.
/// <see cref="ConfigureHandler"/> adjusts the rest, such as TLS to the destinations or connection
/// limits.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("ReverseProxyOptions")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class ReverseProxyOptions
{
    private readonly List<ReverseProxyRouteEntry> _entries = new();
    private readonly List<ReverseProxyRoute> _routes = new();

    /// <summary>
    /// Gets the routes added so far, in the order they were added, with copies of their lists.
    /// </summary>
    public IReadOnlyList<ReverseProxyRoute> Routes => _routes.AsReadOnly();

    /// <summary>
    /// Gets how the proxy forwards: the forwarded headers it adds, its <c>Via</c> pseudonym, its
    /// activity timeout, the HTTP version it asks the destinations for, and the largest response it
    /// buffers when it cannot stream. <see cref="HttpForwarderOptions.PreserveHost"/> is the default of
    /// every route that leaves <see cref="ReverseProxyRoute.PreserveHost"/> unset.
    /// </summary>
    /// <remarks>
    /// The default <see cref="HttpForwarderOptions.ForwardedHeaderAction"/> is
    /// <see cref="HttpForwardedHeaderAction.Set"/>, right for a proxy that clients reach directly. Behind
    /// another proxy whose chain the destinations read, use <see cref="HttpForwardedHeaderAction.Append"/>.
    /// </remarks>
    public HttpForwarderOptions Forwarder { get; } = new();

    /// <summary>
    /// Gets or sets a callback that adjusts the proxy's <see cref="SocketsHttpHandler"/> after its
    /// proxying defaults are applied, or <see langword="null"/> to keep them.
    /// </summary>
    /// <remarks>
    /// The callback runs while <c>AddReverseProxy</c> runs. It may set anything but what proxying
    /// depends on: when it leaves <see cref="SocketsHttpHandler.AllowAutoRedirect"/> or
    /// <see cref="SocketsHttpHandler.UseCookies"/> on, or <see cref="SocketsHttpHandler.AutomaticDecompression"/>
    /// other than <see cref="System.Net.DecompressionMethods.None"/>, registration fails, because the proxy
    /// would change what it relays.
    /// </remarks>
    public Action<SocketsHttpHandler>? ConfigureHandler { get; set; }

    /// <summary>
    /// Adds a route.
    /// </summary>
    /// <param name="route">The route.</param>
    /// <returns>The same options instance, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The route has no name, a host pattern that is not valid, a path prefix that is not an absolute
    /// path without dot segments, or no destination, or a destination that is not an absolute
    /// <c>http</c> or <c>https</c> URI without a query, a fragment or user information.
    /// </exception>
    /// <exception cref="InvalidOperationException">A route with the same name was already added.</exception>
    public ReverseProxyOptions AddRoute(ReverseProxyRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        ReverseProxyRouteEntry entry = ReverseProxyRouteEntry.Create(route, _entries.Count, nameof(route));

        foreach (ReverseProxyRoute existing in _routes)
        {
            if (string.Equals(existing.Name, entry.Route.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteNameDuplicate, entry.Route.Name));
            }
        }

        _entries.Add(entry);
        _routes.Add(entry.Route);
        return this;
    }

    /// <summary>
    /// Gets the compiled routes the setting starts with.
    /// </summary>
    internal IReadOnlyList<ReverseProxyRouteEntry> Entries => _entries;

    private sealed class DebugView
    {
        private readonly ReverseProxyOptions _target;

        public DebugView(ReverseProxyOptions value)
        {
            _target = value;
        }

        public IReadOnlyList<ReverseProxyRoute> Routes => _target.Routes;
        public HttpForwarderOptions Forwarder => _target.Forwarder;
        public Action<SocketsHttpHandler>? ConfigureHandler => _target.ConfigureHandler;
    }
}
