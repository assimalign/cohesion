using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Proxy;

/// <summary>
/// One route of the reverse proxy: which requests it takes, by host and path prefix, and the
/// destinations it forwards them to.
/// </summary>
/// <remarks>
/// <para>
/// A request is taken by the most specific route that matches it: the route whose host pattern
/// matches most closely (an exact host, then the longest <c>*.suffix</c>, then any host), then the
/// route with the longest path prefix, then the route registered first. A request no route matches
/// goes to the next middleware.
/// </para>
/// <para>
/// A route is an immutable value. The options and the <c>reverse-proxy</c> setting copy its lists
/// when they take it, so changing a list afterwards has no effect; build a new route, for example
/// with a <see langword="with"/> expression, to change one.
/// </para>
/// </remarks>
public sealed record ReverseProxyRoute
{
    /// <summary>
    /// Gets the route's name, unique among the routes, compared with ordinal (case-sensitive)
    /// semantics.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the host patterns the request's effective host must match: an exact host
    /// (<c>api.example.com</c>, <c>[::1]</c>), a wildcard subdomain (<c>*.example.com</c>, which does
    /// not match <c>example.com</c> itself), or <c>*</c> for any host. The port is ignored. Empty, the
    /// default, means any host.
    /// </summary>
    /// <remarks>
    /// The host matched is the effective host: the host a trusted proxy in front of this one
    /// forwarded when <c>UseForwardedHeaders</c> ran first, otherwise the request's own.
    /// </remarks>
    public IReadOnlyList<string> Hosts { get; init; } = [];

    /// <summary>
    /// Gets the path prefix the request's path must start with, on a segment boundary and compared
    /// case-insensitively: <c>/api</c> matches <c>/api</c>, <c>/API/items</c> and <c>/api/</c>, but not
    /// <c>/apis</c>. The default is <c>/</c>, which matches every path.
    /// </summary>
    /// <remarks>
    /// The prefix is an absolute path with no <c>.</c> or <c>..</c> segment, no <c>?</c> and no
    /// <c>#</c>; a trailing <c>/</c> is ignored. The path it is compared with has its dot segments
    /// removed first, so <c>/api/../admin</c> is matched, and forwarded, as <c>/admin</c>.
    /// </remarks>
    public HttpPath PathPrefix { get; init; } = HttpPath.Root;

    /// <summary>
    /// Gets the destinations the route forwards to, taken in turn (round-robin): absolute <c>http</c>
    /// or <c>https</c> URIs with no query, fragment or user information, which may carry a base path
    /// (<c>http://orders.internal:8080/v1</c>). At least one is required.
    /// </summary>
    public required IReadOnlyList<Uri> Destinations { get; init; }

    /// <summary>
    /// Gets a value indicating whether the route removes <see cref="PathPrefix"/> from the path it
    /// forwards: with the prefix <c>/orders</c>, <c>/orders/42</c> is forwarded as <c>/42</c> and
    /// <c>/orders</c> as <c>/</c>. The default is <see langword="false"/>, which forwards the whole
    /// path.
    /// </summary>
    public bool StripPrefix { get; init; }

    /// <summary>
    /// Gets a value indicating whether the forwarded request keeps the client's <c>Host</c>
    /// (<see langword="true"/>) or names the destination's authority (<see langword="false"/>), or
    /// <see langword="null"/>, the default, to use <see cref="HttpForwarderOptions.PreserveHost"/> of
    /// <see cref="ReverseProxyOptions.Forwarder"/>.
    /// </summary>
    public bool? PreserveHost { get; init; }
}
