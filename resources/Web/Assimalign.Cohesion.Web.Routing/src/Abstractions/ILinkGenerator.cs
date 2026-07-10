using System;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Generates outbound URLs — relative paths and absolute URIs — from registered routes and route
/// values.
/// </summary>
/// <remarks>
/// <para>
/// Generation is the outbound half of routing. A route is addressed either <em>by name</em>
/// (registered through <see cref="IRouteNameMetadata"/>; unique per router, compared
/// case-insensitively) or <em>by values</em>, where every route whose parameters, required values,
/// and parameter policies are satisfiable by the supplied values is a candidate and the most
/// specific one wins: candidates are ordered by descending
/// <see cref="Patterns.RoutePattern.OutboundPrecedence"/>, with registration order breaking ties so
/// selection is deterministic.
/// </para>
/// <para>
/// Generated paths honor the route template: parameter values are filled from the supplied route
/// values first and pattern defaults second; omitted optional and catch-all segments collapse;
/// a trailing run of segments whose values equal their defaults is trimmed; and parameter policies
/// (inline constraints) are re-validated so a generated URL always matches the route it was
/// generated from. Values are percent-encoded per RFC 3986 with path-segment rules — a catch-all
/// declared as <c>{*name}</c> encodes slashes in its value while <c>{**name}</c> keeps them as
/// segment separators. Supplied values that do not correspond to a route parameter are appended as
/// a query string using query encoding, in the order they were supplied.
/// </para>
/// </remarks>
public interface ILinkGenerator
{
    /// <summary>
    /// Attempts to generate a relative path (with any surplus values as a query string) for the
    /// route registered under the supplied name.
    /// </summary>
    /// <param name="routeName">The case-insensitive route name to address.</param>
    /// <param name="values">The route values used to fill route parameters; surplus entries are appended as a query string. May be <see langword="null"/> when the route needs no values.</param>
    /// <param name="path">The generated path when generation succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a route with the supplied name exists and a path could be generated from the values; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routeName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="routeName"/> is empty.</exception>
    bool TryGetPathByName(string routeName, RouteValueDictionary? values, [NotNullWhen(true)] out string? path);

    /// <summary>
    /// Generates a relative path (with any surplus values as a query string) for the route
    /// registered under the supplied name.
    /// </summary>
    /// <param name="routeName">The case-insensitive route name to address.</param>
    /// <param name="values">The route values used to fill route parameters; surplus entries are appended as a query string. May be <see langword="null"/> when the route needs no values.</param>
    /// <returns>The generated path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routeName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="routeName"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">No route is registered under <paramref name="routeName"/>, or a path could not be generated from the supplied values.</exception>
    string GetPathByName(string routeName, RouteValueDictionary? values = null);

    /// <summary>
    /// Attempts to generate an absolute URI for the route registered under the supplied name.
    /// </summary>
    /// <param name="routeName">The case-insensitive route name to address.</param>
    /// <param name="scheme">The URI scheme; must be <see cref="HttpScheme.Http"/> or <see cref="HttpScheme.Https"/>.</param>
    /// <param name="host">The authority (host and optional port) of the generated URI.</param>
    /// <param name="values">The route values used to fill route parameters; surplus entries are appended as a query string. May be <see langword="null"/> when the route needs no values.</param>
    /// <param name="uri">The generated absolute URI when generation succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a route with the supplied name exists and a URI could be generated from the values; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routeName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="routeName"/> is empty, <paramref name="scheme"/> is not <see cref="HttpScheme.Http"/> or <see cref="HttpScheme.Https"/>, or <paramref name="host"/> is empty.</exception>
    bool TryGetUriByName(string routeName, HttpScheme scheme, HttpHost host, RouteValueDictionary? values, [NotNullWhen(true)] out string? uri);

    /// <summary>
    /// Generates an absolute URI for the route registered under the supplied name.
    /// </summary>
    /// <param name="routeName">The case-insensitive route name to address.</param>
    /// <param name="scheme">The URI scheme; must be <see cref="HttpScheme.Http"/> or <see cref="HttpScheme.Https"/>.</param>
    /// <param name="host">The authority (host and optional port) of the generated URI.</param>
    /// <param name="values">The route values used to fill route parameters; surplus entries are appended as a query string. May be <see langword="null"/> when the route needs no values.</param>
    /// <returns>The generated absolute URI.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="routeName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="routeName"/> is empty, <paramref name="scheme"/> is not <see cref="HttpScheme.Http"/> or <see cref="HttpScheme.Https"/>, or <paramref name="host"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">No route is registered under <paramref name="routeName"/>, or a path could not be generated from the supplied values.</exception>
    string GetUriByName(string routeName, HttpScheme scheme, HttpHost host, RouteValueDictionary? values = null);

    /// <summary>
    /// Attempts to generate a relative path from route values alone, selecting the most specific
    /// route the values can satisfy.
    /// </summary>
    /// <param name="values">The route values used to select a route and fill its parameters; surplus entries are appended as a query string.</param>
    /// <param name="path">The generated path when generation succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when at least one route could be generated from the values; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Candidates are evaluated in descending <see cref="Patterns.RoutePattern.OutboundPrecedence"/>
    /// order (more specific first), with registration order breaking ties; the first candidate whose
    /// parameters, required values, and parameter policies the supplied values satisfy produces the
    /// path.
    /// </remarks>
    bool TryGetPathByValues(RouteValueDictionary values, [NotNullWhen(true)] out string? path);

    /// <summary>
    /// Attempts to generate an absolute URI from route values alone, selecting the most specific
    /// route the values can satisfy.
    /// </summary>
    /// <param name="scheme">The URI scheme; must be <see cref="HttpScheme.Http"/> or <see cref="HttpScheme.Https"/>.</param>
    /// <param name="host">The authority (host and optional port) of the generated URI.</param>
    /// <param name="values">The route values used to select a route and fill its parameters; surplus entries are appended as a query string.</param>
    /// <param name="uri">The generated absolute URI when generation succeeds; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when at least one route could be generated from the values; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="scheme"/> is not <see cref="HttpScheme.Http"/> or <see cref="HttpScheme.Https"/>, or <paramref name="host"/> is empty.</exception>
    bool TryGetUriByValues(HttpScheme scheme, HttpHost host, RouteValueDictionary values, [NotNullWhen(true)] out string? uri);
}
