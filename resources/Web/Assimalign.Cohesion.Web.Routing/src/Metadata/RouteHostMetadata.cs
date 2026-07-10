using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Routing.Exceptions;

namespace Assimalign.Cohesion.Web.Routing.Metadata;

/// <summary>
/// Endpoint metadata declaring the hosts a route accepts. Attach an instance to a route's
/// <see cref="IRouterRouteMetadataCollection"/> to constrain the route to specific hosts;
/// the router consults it during candidate selection, and a request whose host satisfies
/// none of the constraints skips the route entirely, falling through to other candidates.
/// </summary>
/// <remarks>
/// <para>
/// The router resolves this metadata with last-wins semantics
/// (<see cref="IRouterRouteMetadataCollection.GetMetadata{TMetadata}"/>), so an endpoint-level
/// declaration overrides a broader (e.g. group-level) one rather than combining with it. An
/// empty <see cref="Hosts"/> list declares no constraint — the route accepts any host and
/// ranks as host-unconstrained.
/// </para>
/// <para>
/// This sealed carrier <em>is</em> the metadata contract — there is deliberately no
/// <c>IRouteHostMetadata</c> interface. Metadata items in the bag are immutable data
/// carriers, and the sealed type guarantees the parse-once, immutable host list the router
/// snapshots at construction. Host patterns are parsed once here and reused for every
/// request, so a malformed pattern fails at the producer rather than at match time.
/// </para>
/// </remarks>
public sealed class RouteHostMetadata
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

    /// <summary>
    /// Gets the parsed host constraints the request host is tested against. A request
    /// satisfies the metadata when it matches <em>any</em> constraint in the list.
    /// </summary>
    public IReadOnlyList<RouteHostConstraint> Hosts => _hosts;

    /// <summary>
    /// Returns the declared constraints as comma-separated canonical text, for diagnostics.
    /// </summary>
    /// <returns>The constraint list text, or <c>(any host)</c> when no constraint is declared.</returns>
    public override string ToString() => _hosts.Length == 0 ? "(any host)" : string.Join(", ", _hosts);
}
