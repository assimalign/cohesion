using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Default <see cref="ILinkGenerator"/> built over a router's route table.
/// </summary>
/// <remarks>
/// Construction indexes the pattern-based routes by name (unique, case-insensitive — a duplicate
/// throws, which is what makes duplicate route names fail at build time) and orders the by-values
/// candidates by descending <see cref="RoutePattern.OutboundPrecedence"/> with registration order
/// breaking ties. Generation never uses reflection: values are stringified with the invariant
/// culture and parameter policies are re-validated through the same policy objects the matcher
/// uses (with no HTTP context, which the policy contract permits).
/// </remarks>
internal sealed class RouterLinkGenerator : ILinkGenerator
{
    private readonly OutboundCandidate[] _candidates;
    private readonly Dictionary<string, OutboundCandidate> _candidatesByName;

    internal RouterLinkGenerator(IReadOnlyList<IRouterRoute> routes)
    {
        var candidates = new List<OutboundCandidate>(routes.Count);
        var candidatesByName = new Dictionary<string, OutboundCandidate>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < routes.Count; index++)
        {
            IRouterRoute route = routes[index];
            RoutePattern? pattern = route.Pattern;
            string? name = route.Metadata.GetMetadata<IRouteNameMetadata>()?.RouteName;

            if (pattern is null)
            {
                if (name is not null)
                {
                    throw new InvalidOperationException(
                        $"The route named '{name}' does not expose a route pattern and cannot participate in URL generation.");
                }

                // A fully custom matcher without a pattern is not addressable for generation.
                continue;
            }

            RouteParameterPolicyMap policyMap = route is Route concreteRoute
                ? concreteRoute.PolicyMap
                : RouteParameterPolicyMap.CreateDefault();

            var candidate = new OutboundCandidate(pattern, policyMap, index);

            if (name is not null && !candidatesByName.TryAdd(name, candidate))
            {
                throw new InvalidOperationException(
                    $"The route name '{name}' is registered more than once ('{candidatesByName[name].Pattern.DebuggerToString()}' and '{pattern.DebuggerToString()}'). Route names must be unique.");
            }

            candidates.Add(candidate);
        }

        // Higher outbound precedence is more specific for URL generation and is preferred; the
        // registration index makes the order a deterministic total order (Array.Sort is unstable).
        candidates.Sort(static (left, right) =>
        {
            int result = right.Pattern.OutboundPrecedence.CompareTo(left.Pattern.OutboundPrecedence);
            return result != 0 ? result : left.Index.CompareTo(right.Index);
        });

        _candidates = candidates.ToArray();
        _candidatesByName = candidatesByName;
    }

    /// <inheritdoc />
    public bool TryGetPathByName(string routeName, RouteValueDictionary? values, [NotNullWhen(true)] out string? path)
    {
        ArgumentException.ThrowIfNullOrEmpty(routeName);

        path = null;
        return _candidatesByName.TryGetValue(routeName, out OutboundCandidate candidate)
            && TryBuildLink(candidate, values, out path);
    }

    /// <inheritdoc />
    public string GetPathByName(string routeName, RouteValueDictionary? values = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(routeName);

        if (!_candidatesByName.TryGetValue(routeName, out OutboundCandidate candidate))
        {
            throw new InvalidOperationException($"No route named '{routeName}' has been registered.");
        }

        if (!TryBuildLink(candidate, values, out string? path))
        {
            throw new InvalidOperationException(
                $"A path could not be generated for the route named '{routeName}' ('{candidate.Pattern.DebuggerToString()}') from the supplied route values.");
        }

        return path;
    }

    /// <inheritdoc />
    public bool TryGetUriByName(string routeName, HttpScheme scheme, HttpHost host, RouteValueDictionary? values, [NotNullWhen(true)] out string? uri)
    {
        string schemeText = GetSchemeText(scheme);
        ValidateHost(host);

        uri = null;
        if (!TryGetPathByName(routeName, values, out string? path))
        {
            return false;
        }

        uri = string.Concat(schemeText, "://", host.Value, path);
        return true;
    }

    /// <inheritdoc />
    public string GetUriByName(string routeName, HttpScheme scheme, HttpHost host, RouteValueDictionary? values = null)
    {
        string schemeText = GetSchemeText(scheme);
        ValidateHost(host);

        return string.Concat(schemeText, "://", host.Value, GetPathByName(routeName, values));
    }

    /// <inheritdoc />
    public bool TryGetPathByValues(RouteValueDictionary values, [NotNullWhen(true)] out string? path)
    {
        ArgumentNullException.ThrowIfNull(values);

        for (int index = 0; index < _candidates.Length; index++)
        {
            if (TryBuildLink(_candidates[index], values, out path))
            {
                return true;
            }
        }

        path = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetUriByValues(HttpScheme scheme, HttpHost host, RouteValueDictionary values, [NotNullWhen(true)] out string? uri)
    {
        string schemeText = GetSchemeText(scheme);
        ValidateHost(host);

        uri = null;
        if (!TryGetPathByValues(values, out string? path))
        {
            return false;
        }

        uri = string.Concat(schemeText, "://", host.Value, path);
        return true;
    }

    private static string GetSchemeText(HttpScheme scheme)
    {
        return scheme switch
        {
            HttpScheme.Http => "http",
            HttpScheme.Https => "https",
            _ => throw new ArgumentException("An absolute URI requires the http or https scheme.", nameof(scheme)),
        };
    }

    private static void ValidateHost(HttpHost host)
    {
        if (string.IsNullOrEmpty(host.Value))
        {
            throw new ArgumentException("An absolute URI requires a non-empty host.", nameof(host));
        }
    }

    private static bool TryBuildLink(in OutboundCandidate candidate, RouteValueDictionary? values, [NotNullWhen(true)] out string? link)
    {
        link = null;
        RoutePattern pattern = candidate.Pattern;

        if (!SatisfiesRequiredValues(pattern, values) || !SatisfiesNonParameterDefaults(pattern, values))
        {
            return false;
        }

        // Resolve a value for every parameter: the supplied values win, then the pattern defaults;
        // a parameter with neither must be optional or a catch-all for its segment to collapse.
        var accepted = new RouteValueDictionary();

        foreach (RoutePatternParameterSegment parameter in pattern.Parameters)
        {
            if (values is not null &&
                values.TryGetValue(parameter.Name, out object? supplied) &&
                !IsNullOrEmptyRouteValue(supplied))
            {
                accepted[parameter.Name] = supplied;
                continue;
            }

            if (pattern.Defaults.TryGetValue(parameter.Name, out object? defaultValue) &&
                !IsNullOrEmptyRouteValue(defaultValue))
            {
                accepted[parameter.Name] = defaultValue;
                continue;
            }

            if (!parameter.IsOptional && !parameter.IsCatchAll)
            {
                return false;
            }
        }

        // Re-validate parameter policies so a generated URL always matches the route it was
        // generated from (a candidate the values cannot inbound-match is rejected here, letting a
        // less specific by-values candidate produce the link instead).
        if (!ValidatePolicies(candidate, accepted))
        {
            return false;
        }

        var rendered = new List<(string Text, bool Trimmable)>(pattern.PathSegments.Count);
        bool collapsed = false;

        foreach (RoutePatternPathSegment pathSegment in pattern.PathSegments)
        {
            if (!TryRenderSegment(pathSegment, pattern, accepted, out string? text, out bool trimmable))
            {
                return false;
            }

            if (text is null)
            {
                // The segment collapsed (omitted optional / catch-all). Everything after it must
                // collapse too or the path would have a hole in it.
                collapsed = true;
                continue;
            }

            if (collapsed)
            {
                return false;
            }

            rendered.Add((text, trimmable));
        }

        // Trim the trailing run of segments whose values equal their defaults, so a request for the
        // canonical short form is generated (and defaults re-apply symmetrically when it is matched).
        int segmentCount = rendered.Count;
        while (segmentCount > 0 && rendered[segmentCount - 1].Trimmable)
        {
            segmentCount--;
        }

        var builder = new StringBuilder();
        for (int index = 0; index < segmentCount; index++)
        {
            builder.Append('/');
            builder.Append(rendered[index].Text);
        }

        if (builder.Length == 0)
        {
            builder.Append('/');
        }

        AppendQueryString(builder, pattern, values);

        link = builder.ToString();
        return true;
    }

    private static bool SatisfiesRequiredValues(RoutePattern pattern, RouteValueDictionary? values)
    {
        foreach (KeyValuePair<string, object?> required in pattern.RequiredValues)
        {
            object? current = null;
            bool hasValue = values is not null && values.TryGetValue(required.Key, out current);

            if (!hasValue)
            {
                hasValue = pattern.Defaults.TryGetValue(required.Key, out current);
            }

            if (RoutePattern.IsRequiredValueAny(required.Value))
            {
                if (!hasValue || IsNullOrEmptyRouteValue(current))
                {
                    return false;
                }

                continue;
            }

            if (!hasValue || !RouteValueEqualityComparer.Default.Equals(current, required.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SatisfiesNonParameterDefaults(RoutePattern pattern, RouteValueDictionary? values)
    {
        if (values is null)
        {
            return true;
        }

        // A default that does not correspond to a template parameter behaves like a literal
        // requirement: a supplied value for it must agree or the route is not usable for the values.
        foreach (KeyValuePair<string, object?> defaultValue in pattern.Defaults)
        {
            if (pattern.GetParameter(defaultValue.Key) is not null)
            {
                continue;
            }

            if (values.TryGetValue(defaultValue.Key, out object? supplied) &&
                !RouteValueEqualityComparer.Default.Equals(supplied, defaultValue.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidatePolicies(in OutboundCandidate candidate, RouteValueDictionary accepted)
    {
        foreach (RoutePatternParameterSegment parameter in candidate.Pattern.Parameters)
        {
            if (!accepted.ContainsKey(parameter.Name))
            {
                // Omitted optional / catch-all: the segment collapses, so there is no value to police.
                continue;
            }

            foreach (RoutePatternParameterPolicyReference reference in parameter.ParameterPolicies)
            {
                if (!candidate.PolicyMap.TryResolve(reference, out RouteParameterPolicy? policy) || policy is null)
                {
                    return false;
                }

                RouteParameterPolicyContext policyContext = new(httpContext: null, parameter, accepted);
                if (!policy.Applies(policyContext))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryRenderSegment(
        RoutePatternPathSegment pathSegment,
        RoutePattern pattern,
        RouteValueDictionary accepted,
        out string? text,
        out bool trimmable)
    {
        text = null;
        trimmable = false;

        if (pathSegment.Segments.Count == 1)
        {
            RoutePatternSegment part = pathSegment.Segments[0];

            if (part is RoutePatternLiteralSegment literal)
            {
                text = literal.Content;
                return true;
            }

            if (part is RoutePatternParameterSegment parameter)
            {
                if (!accepted.TryGetValue(parameter.Name, out object? value))
                {
                    // Omitted optional / catch-all: the segment collapses.
                    return true;
                }

                text = EncodeParameterValue(parameter, ToRouteValueString(value));
                trimmable = pattern.Defaults.TryGetValue(parameter.Name, out object? defaultValue) &&
                    RouteValueEqualityComparer.Default.Equals(value, defaultValue);
                return true;
            }

            // A lone separator part is not producible by the parser.
            return false;
        }

        var builder = new StringBuilder();
        IReadOnlyList<RoutePatternSegment> parts = pathSegment.Segments;

        for (int index = 0; index < parts.Count; index++)
        {
            switch (parts[index])
            {
                case RoutePatternLiteralSegment literal:
                    builder.Append(literal.Content);
                    break;

                case RoutePatternSeparatorSegment separator:
                    if (index == parts.Count - 2 &&
                        parts[index + 1] is RoutePatternParameterSegment trailing &&
                        !accepted.ContainsKey(trailing.Name))
                    {
                        // A trailing optional parameter with no value drops together with the
                        // separator that precedes it (e.g. '{name}.{ext?}' without an extension).
                        index++;
                        break;
                    }

                    builder.Append(separator.Content);
                    break;

                case RoutePatternParameterSegment parameter:
                    if (!accepted.TryGetValue(parameter.Name, out object? value))
                    {
                        // Only a trailing optional (handled with its separator above) may collapse
                        // inside a multi-part segment.
                        return false;
                    }

                    builder.Append(EncodeParameterValue(parameter, ToRouteValueString(value)));
                    break;

                default:
                    return false;
            }
        }

        text = builder.ToString();
        return true;
    }

    private static void AppendQueryString(StringBuilder builder, RoutePattern pattern, RouteValueDictionary? values)
    {
        if (values is null)
        {
            return;
        }

        bool first = true;

        foreach (KeyValuePair<string, object?> pair in values)
        {
            if (pair.Value is null ||
                pattern.GetParameter(pair.Key) is not null ||
                pattern.Defaults.ContainsKey(pair.Key))
            {
                // Path parameters and (non-parameter) default requirements are consumed by the
                // route; only genuinely surplus values become query parameters.
                continue;
            }

            builder.Append(first ? '?' : '&');
            first = false;

            builder.Append(Uri.EscapeDataString(pair.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(ToRouteValueString(pair.Value)));
        }
    }

    private static string EncodeParameterValue(RoutePatternParameterSegment parameter, string valueText)
    {
        if (parameter.IsCatchAll && !parameter.EncodeSlashes)
        {
            // '{**name}': slashes remain segment separators; each slash-delimited piece is encoded.
            string[] pieces = valueText.Split('/');
            for (int index = 0; index < pieces.Length; index++)
            {
                pieces[index] = Uri.EscapeDataString(pieces[index]);
            }

            return string.Join('/', pieces);
        }

        // '{name}' and '{*name}': the whole value is one path segment, so '/' is encoded too.
        return Uri.EscapeDataString(valueText);
    }

    private static string ToRouteValueString(object? value)
    {
        return value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool IsNullOrEmptyRouteValue(object? value)
    {
        return value is null || ToRouteValueString(value).Length == 0;
    }

    private readonly struct OutboundCandidate
    {
        public OutboundCandidate(RoutePattern pattern, RouteParameterPolicyMap policyMap, int index)
        {
            Pattern = pattern;
            PolicyMap = policyMap;
            Index = index;
        }

        public RoutePattern Pattern { get; }

        public RouteParameterPolicyMap PolicyMap { get; }

        public int Index { get; }
    }
}
