using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

/// <summary>The outcome of matching a request against the control-plane route table.</summary>
internal enum ControlPlaneRouteMatchStatus
{
    /// <summary>No template matched the request path (404).</summary>
    NoMatch,

    /// <summary>A template matched the path and accepted the method.</summary>
    Matched,

    /// <summary>A template matched the path but none accepted the method (405).</summary>
    MethodNotAllowed,
}

/// <summary>The result of <see cref="ControlPlaneRouter.Match(HttpMethod, HttpPath)"/>.</summary>
internal readonly struct ControlPlaneRouteMatch
{
    private static readonly IReadOnlyDictionary<string, string> _noValues =
        ReadOnlyDictionary<string, string>.Empty;

    private ControlPlaneRouteMatch(
        ControlPlaneRouteMatchStatus status,
        ControlPlaneRoute? route,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<HttpMethod> allowedMethods)
    {
        Status = status;
        Route = route;
        Values = values;
        AllowedMethods = allowedMethods;
    }

    /// <summary>Gets a result for a path no template matched.</summary>
    public static ControlPlaneRouteMatch Unmatched { get; } = new(
        ControlPlaneRouteMatchStatus.NoMatch,
        route: null,
        _noValues,
        Array.Empty<HttpMethod>());

    /// <summary>Gets the match outcome.</summary>
    public ControlPlaneRouteMatchStatus Status { get; }

    /// <summary>Gets the matched route when <see cref="Status"/> is <see cref="ControlPlaneRouteMatchStatus.Matched"/>.</summary>
    public ControlPlaneRoute? Route { get; }

    /// <summary>Gets the captured parameter values; empty unless the request matched.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>
    /// Gets the acceptable methods, in route order, when <see cref="Status"/> is
    /// <see cref="ControlPlaneRouteMatchStatus.MethodNotAllowed"/>; otherwise empty.
    /// </summary>
    public IReadOnlyList<HttpMethod> AllowedMethods { get; }

    /// <summary>Creates a successful match.</summary>
    /// <param name="route">The matched route.</param>
    /// <param name="values">The captured parameter values.</param>
    /// <returns>A <see cref="ControlPlaneRouteMatchStatus.Matched"/> result.</returns>
    public static ControlPlaneRouteMatch Matched(
        ControlPlaneRoute route,
        IReadOnlyDictionary<string, string> values) =>
        new(ControlPlaneRouteMatchStatus.Matched, route, values, Array.Empty<HttpMethod>());

    /// <summary>Creates a method-mismatch result.</summary>
    /// <param name="allowedMethods">The methods the matched path accepts.</param>
    /// <returns>A <see cref="ControlPlaneRouteMatchStatus.MethodNotAllowed"/> result.</returns>
    public static ControlPlaneRouteMatch MethodNotAllowed(IReadOnlyList<HttpMethod> allowedMethods) =>
        new(ControlPlaneRouteMatchStatus.MethodNotAllowed, route: null, _noValues, allowedMethods);
}
