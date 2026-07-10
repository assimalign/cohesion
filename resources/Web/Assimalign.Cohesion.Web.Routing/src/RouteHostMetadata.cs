using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Routing.Exceptions;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Default immutable <see cref="IRouteHostMetadata"/> implementation. Attach an instance to a
/// route's <see cref="IRouterRouteMetadataCollection"/> to constrain the route to specific hosts.
/// </summary>
/// <remarks>
/// Like <see cref="RouterRouteMetadataCollection"/>, this is a public concrete companion to its
/// interface because producers in other assemblies (route mapping, route groups, source
/// generators) must construct it. Host patterns are parsed once at construction and reused for
/// every request, so a malformed pattern fails at the producer rather than at match time.
/// </remarks>
public sealed class RouteHostMetadata : IRouteHostMetadata
{
    private readonly RouteHostConstraint[] _hosts;

    /// <summary>
    /// Initializes the metadata from raw host-constraint patterns
    /// (e.g. <c>"api.example.com"</c>, <c>"*.example.com"</c>, <c>"localhost:5000"</c>, <c>"[::1]"</c>).
    /// </summary>
    /// <param name="hosts">The host patterns to parse. An empty sequence declares no constraint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hosts"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="hosts"/> contains a <see langword="null"/> entry.</exception>
    /// <exception cref="RoutePatternException">An entry is not a well-formed host constraint.</exception>
    public RouteHostMetadata(params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        _hosts = new RouteHostConstraint[hosts.Length];

        for (int index = 0; index < hosts.Length; index++)
        {
            if (hosts[index] is null)
            {
                throw new ArgumentException("Host patterns must not be null.", nameof(hosts));
            }

            _hosts[index] = RouteHostConstraint.Parse(hosts[index]);
        }
    }

    /// <summary>
    /// Initializes the metadata from already parsed host constraints.
    /// </summary>
    /// <param name="hosts">The host constraints. An empty sequence declares no constraint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hosts"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="hosts"/> contains a default-initialized constraint.</exception>
    public RouteHostMetadata(IEnumerable<RouteHostConstraint> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);

        _hosts = hosts is RouteHostConstraint[] array
            ? (RouteHostConstraint[])array.Clone()
            : new List<RouteHostConstraint>(hosts).ToArray();

        for (int index = 0; index < _hosts.Length; index++)
        {
            if (_hosts[index].Host.Length == 0)
            {
                throw new ArgumentException("Host constraints must be created through RouteHostConstraint.Parse or TryParse; a default-initialized constraint matches nothing.", nameof(hosts));
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RouteHostConstraint> Hosts => _hosts;

    /// <summary>
    /// Returns the declared constraints as comma-separated canonical text, for diagnostics.
    /// </summary>
    /// <returns>The constraint list text, or <c>(any host)</c> when no constraint is declared.</returns>
    public override string ToString() => _hosts.Length == 0 ? "(any host)" : string.Join(", ", _hosts);
}
