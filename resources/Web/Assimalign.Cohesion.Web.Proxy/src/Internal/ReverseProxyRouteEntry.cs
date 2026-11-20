using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy.Internal;

/// <summary>
/// A validated reverse-proxy route compiled for matching: its host patterns, its normalized prefix,
/// and the round-robin position among its destinations.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("{Route.Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ReverseProxyRouteEntry
{
    private readonly ProxyHostPattern[] _hosts;
    private readonly string _prefix;
    private readonly Uri[] _destinations;
    private int _next;

    private ReverseProxyRouteEntry(ReverseProxyRoute route, ProxyHostPattern[] hosts, string prefix, Uri[] destinations)
    {
        Route = route;
        _hosts = hosts;
        _prefix = prefix;
        _destinations = destinations;
    }

    /// <summary>
    /// Gets the route, with copies of the lists it was given.
    /// </summary>
    public ReverseProxyRoute Route { get; }

    /// <summary>
    /// Gets the length of the normalized prefix, which ranks routes whose hosts match equally:
    /// zero for <c>/</c>.
    /// </summary>
    public int PrefixLength => _prefix.Length;

    /// <summary>
    /// Validates and compiles a route.
    /// </summary>
    /// <param name="route">The route, not <see langword="null"/>.</param>
    /// <param name="index">The route's position, which names a route that has no name.</param>
    /// <param name="paramName">The parameter the route came from, for the exception.</param>
    /// <exception cref="ArgumentException">The route is not valid.</exception>
    public static ReverseProxyRouteEntry Create(ReverseProxyRoute route, int index, string paramName)
    {
        string name = route.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(Format(ErrorMessages.RouteNameMissing, index), paramName);
        }

        IReadOnlyList<string>? hosts = route.Hosts;
        if (hosts is null)
        {
            throw new ArgumentException(Format(ErrorMessages.RouteHostsNull, name), paramName);
        }

        ProxyHostPattern[] patterns = new ProxyHostPattern[hosts.Count];
        string[] hostsCopy = new string[hosts.Count];
        for (int i = 0; i < patterns.Length; i++)
        {
            string pattern = hosts[i];
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException(Format(ErrorMessages.RouteHostPatternEmpty, name), paramName);
            }

            try
            {
                patterns[i] = ProxyHostPattern.Create(pattern);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(Format(ErrorMessages.RouteHostPatternInvalid, name, exception.Message), paramName, exception);
            }

            hostsCopy[i] = pattern;
        }

        if (!TryNormalizePrefix(route.PathPrefix, out string prefix))
        {
            throw new ArgumentException(Format(ErrorMessages.RoutePathPrefixInvalid, name, route.PathPrefix.Value), paramName);
        }

        IReadOnlyList<Uri>? destinations = route.Destinations;
        if (destinations is null || destinations.Count == 0)
        {
            throw new ArgumentException(Format(ErrorMessages.RouteDestinationsMissing, name), paramName);
        }

        Uri[] destinationsCopy = new Uri[destinations.Count];
        for (int i = 0; i < destinationsCopy.Length; i++)
        {
            Uri destination = destinations[i];
            if (!HttpForwarder.IsValidDestinationPrefix(destination))
            {
                throw new ArgumentException(Format(ErrorMessages.RouteDestinationInvalid, name, destination), paramName);
            }

            destinationsCopy[i] = destination;
        }

        ReverseProxyRoute snapshot = route with
        {
            Hosts = Array.AsReadOnly(hostsCopy),
            Destinations = Array.AsReadOnly(destinationsCopy),
        };

        return new ReverseProxyRouteEntry(snapshot, patterns, prefix, destinationsCopy);
    }

    /// <summary>
    /// Ranks how closely the route's host patterns match <paramref name="host"/>.
    /// </summary>
    /// <returns>
    /// The rank of the closest pattern that matches (<see cref="ProxyHostPattern.AnyRank"/> for a route
    /// that names no host), or <c>-1</c> when none matches.
    /// </returns>
    public int MatchHost(HttpHost host)
    {
        if (_hosts.Length == 0)
        {
            return ProxyHostPattern.AnyRank;
        }

        int best = -1;
        foreach (ProxyHostPattern pattern in _hosts)
        {
            if (pattern.Rank > best && pattern.Matcher.IsMatch(host))
            {
                best = pattern.Rank;
            }
        }

        return best;
    }

    /// <summary>
    /// Determines whether <paramref name="path"/> starts with the route's prefix on a segment boundary,
    /// ignoring case as Web routing does.
    /// </summary>
    public bool MatchPath(string path)
    {
        if (_prefix.Length == 0)
        {
            return true;
        }

        return path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
            && (path.Length == _prefix.Length || path[_prefix.Length] == '/');
    }

    /// <summary>
    /// Gets the path to forward for a request whose normalized path the route matched: the path
    /// itself, or the part below the prefix when the route strips it.
    /// </summary>
    public string GetForwardedPath(string path)
    {
        if (!Route.StripPrefix || _prefix.Length == 0)
        {
            return path;
        }

        // Ordinal-ignore-case matching folds one UTF-16 unit at a time, so the matched part of the path
        // is exactly as long as the prefix.
        return path.Length == _prefix.Length ? "/" : path[_prefix.Length..];
    }

    /// <summary>
    /// Takes the next destination in turn.
    /// </summary>
    public Uri NextDestination()
    {
        if (_destinations.Length == 1)
        {
            return _destinations[0];
        }

        // Read as unsigned, the counter keeps counting up past int.MaxValue and never yields a negative
        // index. It wraps to zero after 2^32 turns, where one destination can take two turns in a row.
        uint turn = unchecked((uint)Interlocked.Increment(ref _next) - 1u);
        return _destinations[turn % (uint)_destinations.Length];
    }

    /// <summary>
    /// Normalizes a route prefix: an absolute path with no dot segment, <c>?</c> or <c>#</c>, without its
    /// trailing <c>/</c>, so <c>/</c> becomes the empty string that matches every path.
    /// </summary>
    private static bool TryNormalizePrefix(HttpPath prefix, out string normalized)
    {
        // HttpPath refused '?', '#' and a relative path when the prefix was converted; the asterisk
        // form it still allows is no prefix.
        string value = string.IsNullOrEmpty(prefix.Value) ? "/" : prefix.Value;

        if (value[0] != '/'
            || !HttpForwarder.TryNormalizePath(new HttpPath(value), out HttpPath result)
            || !string.Equals(result.Value, value, StringComparison.Ordinal))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = value.TrimEnd('/');
        return true;
    }

    private static string Format(string format, params object?[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    private sealed class DebugView
    {
        private readonly ReverseProxyRouteEntry _target;

        public DebugView(ReverseProxyRouteEntry value)
        {
            _target = value;
        }

        public ReverseProxyRoute Route => _target.Route;
        public int PrefixLength => _target.PrefixLength;
    }
}
