using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

/// <summary>
/// One fixed control-plane route: an HTTP method, a template of literal and whole-segment
/// <c>{name}</c> parameter segments, and its handler.
/// </summary>
/// <remarks>
/// Matching reproduces the subset of <c>Web.Routing</c> the control plane used: literal segments
/// compare ordinal case-insensitively, a parameter captures the raw segment text, and the request
/// must have exactly as many segments as the template. No optional, default, catch-all,
/// complex-segment, or constrained parameters exist, so none are supported.
/// </remarks>
internal sealed class ControlPlaneRoute
{
    private static readonly IReadOnlyDictionary<string, string> _noValues =
        ReadOnlyDictionary<string, string>.Empty;

    private readonly string[] _segments;
    private readonly bool[] _parameters;

    /// <summary>Creates a route from a template such as <c>/cohesion/v1/resources/{name}</c>.</summary>
    /// <param name="method">The single HTTP method the route accepts.</param>
    /// <param name="template">The route template.</param>
    /// <param name="handler">The handler invoked when the route matches.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="template"/> is empty, or a segment is neither a literal nor a whole
    /// <c>{name}</c> parameter.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    public ControlPlaneRoute(HttpMethod method, string template, ControlPlaneRouteHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        Method = method;
        Template = template;
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));

        string[] parts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        _segments = new string[parts.Length];
        _parameters = new bool[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.Length > 2 && part[0] == '{' && part[^1] == '}' && IsParameterName(part.AsSpan(1, part.Length - 2)))
            {
                _segments[index] = part[1..^1];
                _parameters[index] = true;
            }
            else if (part.AsSpan().IndexOfAny('{', '}') >= 0)
            {
                throw new ArgumentException(
                    $"Route template segment '{part}' must be a literal or a whole '{{name}}' parameter " +
                    "whose name uses only ASCII letters, digits, and underscores.",
                    nameof(template));
            }
            else
            {
                _segments[index] = part;
            }
        }
    }

    /// <summary>Gets the HTTP method the route accepts.</summary>
    public HttpMethod Method { get; }

    /// <summary>Gets the route template.</summary>
    public string Template { get; }

    /// <summary>Gets the handler invoked when the route matches.</summary>
    public ControlPlaneRouteHandler Handler { get; }

    /// <summary>Matches request path segments against the template, ignoring the method.</summary>
    /// <param name="requestSegments">The request path split on <c>/</c> without empty segments.</param>
    /// <param name="values">The captured parameter values when the path matches.</param>
    /// <returns><see langword="true"/> when every segment matches.</returns>
    public bool TryMatchPath(string[] requestSegments, out IReadOnlyDictionary<string, string> values)
    {
        values = _noValues;
        if (requestSegments.Length != _segments.Length)
        {
            return false;
        }

        Dictionary<string, string>? captured = null;
        for (int index = 0; index < _segments.Length; index++)
        {
            if (_parameters[index])
            {
                captured ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                captured[_segments[index]] = requestSegments[index];
            }
            else if (!string.Equals(_segments[index], requestSegments[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (captured is not null)
        {
            values = captured;
        }

        return true;
    }

    // Rejects the Web.Routing syntaxes this matcher does not implement (optional '?', default '=',
    // constraint ':', catch-all '*'), so such a template fails at construction instead of
    // silently becoming a parameter with an odd name.
    private static bool IsParameterName(ReadOnlySpan<char> name)
    {
        for (int index = 0; index < name.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(name[index]) && name[index] != '_')
            {
                return false;
            }
        }

        return true;
    }
}
