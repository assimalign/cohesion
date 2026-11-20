using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// One value of the <c>reverse-proxy</c> setting: its routes, validated and compiled, in registration
/// order. A table never changes; the setting replaces it whole.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("Count = {Routes.Count}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ReverseProxyRouteTable
{
    private readonly ReverseProxyRouteEntry[] _entries;

    private ReverseProxyRouteTable(ReverseProxyRouteEntry[] entries)
    {
        _entries = entries;

        ReverseProxyRoute[] routes = new ReverseProxyRoute[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            routes[i] = entries[i].Route;
        }

        Routes = Array.AsReadOnly(routes);
    }

    /// <summary>
    /// Gets the routes, with copies of the lists they were given.
    /// </summary>
    public IReadOnlyList<ReverseProxyRoute> Routes { get; }

    /// <summary>
    /// Gets a value indicating whether the table has no route, so every request goes to the next
    /// middleware.
    /// </summary>
    public bool IsEmpty => _entries.Length == 0;

    /// <summary>
    /// Creates a table from routes the options already validated.
    /// </summary>
    public static ReverseProxyRouteTable Create(IReadOnlyList<ReverseProxyRouteEntry> entries)
    {
        ReverseProxyRouteEntry[] copy = new ReverseProxyRouteEntry[entries.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = entries[i];
        }

        return new ReverseProxyRouteTable(copy);
    }

    /// <summary>
    /// Validates and compiles a whole value of the setting.
    /// </summary>
    /// <param name="routes">The routes.</param>
    /// <param name="paramName">The parameter the routes came from, for the exception.</param>
    /// <exception cref="ArgumentException">A route is <see langword="null"/> or not valid, or two routes share a name.</exception>
    public static ReverseProxyRouteTable Create(IReadOnlyList<ReverseProxyRoute> routes, string paramName)
    {
        ReverseProxyRouteEntry[] entries = new ReverseProxyRouteEntry[routes.Count];
        HashSet<string> names = new(StringComparer.Ordinal);

        for (int i = 0; i < entries.Length; i++)
        {
            ReverseProxyRoute? route = routes[i];
            if (route is null)
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteNull, i), paramName);
            }

            ReverseProxyRouteEntry entry = ReverseProxyRouteEntry.Create(route, i, paramName);
            if (!names.Add(entry.Route.Name))
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.RouteNameDuplicate, entry.Route.Name), paramName);
            }

            entries[i] = entry;
        }

        return new ReverseProxyRouteTable(entries);
    }

    /// <summary>
    /// Finds the route that takes a request: the closest host match, then the longest prefix, then the
    /// route registered first.
    /// </summary>
    /// <param name="host">The request's effective host.</param>
    /// <param name="path">The request's path, with its dot segments removed.</param>
    /// <returns>The route, or <see langword="null"/> when none matches.</returns>
    public ReverseProxyRouteEntry? Match(HttpHost host, string path)
    {
        ReverseProxyRouteEntry? best = null;
        int bestHost = -1;

        foreach (ReverseProxyRouteEntry entry in _entries)
        {
            int hostRank = entry.MatchHost(host);
            if (hostRank < 0)
            {
                continue;
            }

            // Entries are in registration order, so an equal rank never replaces the route found first.
            if (best is not null
                && (hostRank < bestHost || (hostRank == bestHost && entry.PrefixLength <= best.PrefixLength)))
            {
                continue;
            }

            if (entry.MatchPath(path))
            {
                best = entry;
                bestHost = hostRank;
            }
        }

        return best;
    }

    private sealed class DebugView
    {
        private readonly ReverseProxyRouteTable _target;

        public DebugView(ReverseProxyRouteTable value)
        {
            _target = value;
        }

        public IReadOnlyList<ReverseProxyRoute> Routes => _target.Routes;
        public bool IsEmpty => _target.IsEmpty;
    }
}
