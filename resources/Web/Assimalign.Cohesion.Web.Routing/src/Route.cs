using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Patterns;
using Assimalign.Cohesion.Web.Routing.Policies;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Represents a concrete route pattern, the HTTP methods it accepts, its handler, and its endpoint metadata.
/// </summary>
/// <remarks>
/// A route can accept more than one HTTP method. Path matching (<see cref="TryMatchPath"/>) is
/// separated from method acceptance so a <see cref="Router"/> can distinguish a 404 (no path
/// matched) from a 405 (a path matched but the method did not).
/// </remarks>
public sealed class Route : IRouterRoute
{
    private static readonly IRouterRouteHandler EmptyHandler = new EmptyRouterRouteHandler();

    private readonly HttpMethod[] _methods;

    /// <summary>
    /// Creates a new route from a raw route pattern.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The raw route pattern.</param>
    public Route(HttpMethod method, string pattern)
        : this(new[] { method }, RoutePatternParser.Parse(pattern), RouteParameterPolicyMap.CreateDefault(), EmptyHandler)
    {
    }

    /// <summary>
    /// Creates a new route from a raw route pattern and mapped handler.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The raw route pattern.</param>
    /// <param name="handler">The handler mapped to the route.</param>
    public Route(HttpMethod method, string pattern, IRouterRouteHandler handler)
        : this(new[] { method }, RoutePatternParser.Parse(pattern), RouteParameterPolicyMap.CreateDefault(), handler)
    {
    }

    /// <summary>
    /// Creates a new route from a raw route pattern, mapped handler, and endpoint metadata.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The raw route pattern.</param>
    /// <param name="handler">The handler mapped to the route.</param>
    /// <param name="metadata">The endpoint metadata attached to the route.</param>
    public Route(HttpMethod method, string pattern, IRouterRouteHandler handler, IRouterRouteMetadataCollection metadata)
        : this(new[] { method }, RoutePatternParser.Parse(pattern), RouteParameterPolicyMap.CreateDefault(), handler, metadata)
    {
    }

    /// <summary>
    /// Creates a new route from a raw route pattern and custom policy map.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The raw route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve parameter policies.</param>
    public Route(HttpMethod method, string pattern, RouteParameterPolicyMap policyMap)
        : this(new[] { method }, RoutePatternParser.Parse(pattern), policyMap, EmptyHandler)
    {
    }

    /// <summary>
    /// Creates a new route from a raw route pattern, custom policy map, and mapped handler.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The raw route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve parameter policies.</param>
    /// <param name="handler">The handler mapped to the route.</param>
    public Route(HttpMethod method, string pattern, RouteParameterPolicyMap policyMap, IRouterRouteHandler handler)
        : this(new[] { method }, RoutePatternParser.Parse(pattern), policyMap, handler)
    {
    }

    /// <summary>
    /// Creates a new route from an already parsed route pattern.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    public Route(HttpMethod method, RoutePattern pattern)
        : this(new[] { method }, pattern, RouteParameterPolicyMap.CreateDefault(), EmptyHandler)
    {
    }

    /// <summary>
    /// Creates a new route from an already parsed route pattern and mapped handler.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    /// <param name="handler">The handler mapped to the route.</param>
    public Route(HttpMethod method, RoutePattern pattern, IRouterRouteHandler handler)
        : this(new[] { method }, pattern, RouteParameterPolicyMap.CreateDefault(), handler)
    {
    }

    /// <summary>
    /// Creates a new route from an already parsed route pattern and custom policy map.
    /// </summary>
    /// <param name="method">The HTTP method accepted by the route.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve parameter policies.</param>
    public Route(HttpMethod method, RoutePattern pattern, RouteParameterPolicyMap policyMap)
        : this(new[] { method }, pattern, policyMap, EmptyHandler)
    {
    }

    /// <summary>
    /// Creates a new route from a raw route pattern accepting multiple HTTP methods.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="pattern">The raw route pattern.</param>
    public Route(IEnumerable<HttpMethod> methods, string pattern)
        : this(methods, RoutePatternParser.Parse(pattern), RouteParameterPolicyMap.CreateDefault(), EmptyHandler)
    {
    }

    /// <summary>
    /// Creates a new route from a raw route pattern and mapped handler accepting multiple HTTP methods.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="pattern">The raw route pattern.</param>
    /// <param name="handler">The handler mapped to the route.</param>
    public Route(IEnumerable<HttpMethod> methods, string pattern, IRouterRouteHandler handler)
        : this(methods, RoutePatternParser.Parse(pattern), RouteParameterPolicyMap.CreateDefault(), handler)
    {
    }

    /// <summary>
    /// Creates a new route from a raw route pattern and custom policy map accepting multiple HTTP methods.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="pattern">The raw route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve parameter policies.</param>
    public Route(IEnumerable<HttpMethod> methods, string pattern, RouteParameterPolicyMap policyMap)
        : this(methods, RoutePatternParser.Parse(pattern), policyMap, EmptyHandler)
    {
    }

    /// <summary>
    /// Creates a new route from a raw route pattern, custom policy map, and mapped handler accepting multiple HTTP methods.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="pattern">The raw route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve parameter policies.</param>
    /// <param name="handler">The handler mapped to the route.</param>
    public Route(IEnumerable<HttpMethod> methods, string pattern, RouteParameterPolicyMap policyMap, IRouterRouteHandler handler)
        : this(methods, RoutePatternParser.Parse(pattern), policyMap, handler)
    {
    }

    /// <summary>
    /// Creates a new route from an already parsed route pattern accepting multiple HTTP methods.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    public Route(IEnumerable<HttpMethod> methods, RoutePattern pattern)
        : this(methods, pattern, RouteParameterPolicyMap.CreateDefault(), EmptyHandler)
    {
    }

    /// <summary>
    /// Creates a new route from an already parsed route pattern and mapped handler accepting multiple HTTP methods.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    /// <param name="handler">The handler mapped to the route.</param>
    public Route(IEnumerable<HttpMethod> methods, RoutePattern pattern, IRouterRouteHandler handler)
        : this(methods, pattern, RouteParameterPolicyMap.CreateDefault(), handler)
    {
    }

    /// <summary>
    /// Creates a new route from an already parsed route pattern and custom policy map accepting multiple HTTP methods.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve parameter policies.</param>
    public Route(IEnumerable<HttpMethod> methods, RoutePattern pattern, RouteParameterPolicyMap policyMap)
        : this(methods, pattern, policyMap, EmptyHandler)
    {
    }

    /// <summary>
    /// Creates a new route from an already parsed route pattern, custom policy map, and mapped handler.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve parameter policies.</param>
    /// <param name="handler">The handler mapped to the route.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="methods"/>, <paramref name="pattern"/>, <paramref name="policyMap"/>,
    /// or <paramref name="handler"/> is <see langword="null"/>.
    /// </exception>
    public Route(IEnumerable<HttpMethod> methods, RoutePattern pattern, RouteParameterPolicyMap policyMap, IRouterRouteHandler handler)
        : this(methods, pattern, policyMap, handler, RouterRouteMetadataCollection.Empty)
    {
    }

    /// <summary>
    /// Creates a new route from an already parsed route pattern, custom policy map, mapped handler, and endpoint metadata.
    /// </summary>
    /// <param name="methods">The HTTP methods accepted by the route. An empty sequence accepts any method.</param>
    /// <param name="pattern">The parsed route pattern.</param>
    /// <param name="policyMap">The policy map used to resolve parameter policies.</param>
    /// <param name="handler">The handler mapped to the route.</param>
    /// <param name="metadata">The endpoint metadata attached to the route.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="methods"/>, <paramref name="pattern"/>, <paramref name="policyMap"/>,
    /// <paramref name="handler"/>, or <paramref name="metadata"/> is <see langword="null"/>.
    /// </exception>
    public Route(IEnumerable<HttpMethod> methods, RoutePattern pattern, RouteParameterPolicyMap policyMap, IRouterRouteHandler handler, IRouterRouteMetadataCollection metadata)
    {
        ArgumentNullException.ThrowIfNull(methods);

        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        PolicyMap = policyMap ?? throw new ArgumentNullException(nameof(policyMap));
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _methods = NormalizeMethods(methods);

        ValidatePatternPolicies(Pattern, PolicyMap);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<HttpMethod> Methods => _methods;

    /// <summary>
    /// Gets the parsed route pattern.
    /// </summary>
    public RoutePattern Pattern { get; }

    /// <summary>
    /// Gets the policy map used to resolve route parameter policies.
    /// </summary>
    public RouteParameterPolicyMap PolicyMap { get; }

    /// <inheritdoc />
    public decimal InboundPrecedence => Pattern.InboundPrecedence;

    /// <inheritdoc />
    public IRouterRouteHandler Handler { get; }

    /// <inheritdoc />
    public IRouterRouteMetadataCollection Metadata { get; }

    /// <summary>
    /// Determines whether the route accepts the supplied HTTP method.
    /// </summary>
    /// <param name="method">The HTTP method to test.</param>
    /// <returns>
    /// <see langword="true"/> when the route accepts the method, or when the route accepts any method
    /// (its <see cref="Methods"/> collection is empty); otherwise <see langword="false"/>.
    /// </returns>
    public bool AcceptsMethod(HttpMethod method)
    {
        if (_methods.Length == 0)
        {
            return true;
        }

        for (int i = 0; i < _methods.Length; i++)
        {
            if (_methods[i] == method)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool TryMatch(IHttpContext httpContext, out RouteValueDictionary values)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!TryMatchPath(httpContext, out values))
        {
            return false;
        }

        if (AcceptsMethod(httpContext.Request.Method))
        {
            return true;
        }

        values = new RouteValueDictionary();
        return false;
    }

    /// <inheritdoc />
    public bool TryMatchPath(IHttpContext httpContext, out RouteValueDictionary values)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        HttpPath path = httpContext.Request.Path;

        values = new RouteValueDictionary();

        string[] requestSegments = SplitPath(path);
        int requestIndex = 0;

        for (int patternIndex = 0; patternIndex < Pattern.PathSegments.Count; patternIndex++)
        {
            RoutePatternPathSegment pathSegment = Pattern.PathSegments[patternIndex];

            if (pathSegment.Segments.Count == 1 &&
                pathSegment.Segments[0] is RoutePatternParameterSegment { IsCatchAll: true } catchAll)
            {
                string remainder = requestIndex >= requestSegments.Length
                    ? string.Empty
                    : string.Join("/", requestSegments[requestIndex..]);

                if (!TryAssignParameter(catchAll, remainder, values))
                {
                    values = new RouteValueDictionary();
                    return false;
                }

                requestIndex = requestSegments.Length;
                break;
            }

            if (requestIndex >= requestSegments.Length)
            {
                if (!TryMatchMissingSegment(pathSegment, values))
                {
                    values = new RouteValueDictionary();
                    return false;
                }

                continue;
            }

            if (!TryMatchSegment(pathSegment, requestSegments[requestIndex], values))
            {
                values = new RouteValueDictionary();
                return false;
            }

            requestIndex++;
        }

        if (requestIndex < requestSegments.Length)
        {
            values = new RouteValueDictionary();
            return false;
        }

        ApplyDefaults(Pattern, values);

        if (!ValidatePolicies(this, httpContext, values))
        {
            values = new RouteValueDictionary();
            return false;
        }

        return true;
    }

    private static HttpMethod[] NormalizeMethods(IEnumerable<HttpMethod> methods)
    {
        List<HttpMethod> normalized = new();

        foreach (HttpMethod method in methods)
        {
            bool exists = false;
            for (int i = 0; i < normalized.Count; i++)
            {
                if (normalized[i] == method)
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                normalized.Add(method);
            }
        }

        return normalized.ToArray();
    }

    private static bool TryMatchMissingSegment(RoutePatternPathSegment pathSegment, RouteValueDictionary values)
    {
        if (pathSegment.Segments.Count != 1 ||
            pathSegment.Segments[0] is not RoutePatternParameterSegment parameter)
        {
            return false;
        }

        return TryAssignParameter(parameter, string.Empty, values);
    }

    private static bool TryMatchSegment(RoutePatternPathSegment pathSegment, string requestSegment, RouteValueDictionary values)
    {
        if (pathSegment.Segments.Count == 1)
        {
            return TryMatchSimpleSegment(pathSegment.Segments[0], requestSegment, values);
        }

        return TryMatchComplexSegment(pathSegment.Segments, 0, requestSegment, 0, values);
    }

    private static bool TryMatchSimpleSegment(RoutePatternSegment pathPart, string requestSegment, RouteValueDictionary values)
    {
        return pathPart switch
        {
            RoutePatternLiteralSegment literal => string.Equals(
                literal.Content,
                requestSegment,
                StringComparison.OrdinalIgnoreCase),

            RoutePatternSeparatorSegment separator => string.Equals(
                separator.Content,
                requestSegment,
                StringComparison.OrdinalIgnoreCase),

            RoutePatternParameterSegment parameter => TryAssignParameter(parameter, requestSegment, values),

            _ => throw new NotSupportedException(),
        };
    }

    private static bool TryMatchComplexSegment(
        IReadOnlyList<RoutePatternSegment> parts,
        int partIndex,
        string requestSegment,
        int requestIndex,
        RouteValueDictionary values)
    {
        if (partIndex == parts.Count)
        {
            return requestIndex == requestSegment.Length;
        }

        if (requestIndex == requestSegment.Length &&
            TryMatchOmittedTrailingOptional(parts, partIndex, values))
        {
            return true;
        }

        RoutePatternSegment currentPart = parts[partIndex];

        if (currentPart is RoutePatternLiteralSegment literal)
        {
            return TryConsumeText(literal.Content, parts, partIndex, requestSegment, requestIndex, values);
        }

        if (currentPart is RoutePatternSeparatorSegment separator)
        {
            return TryConsumeText(separator.Content, parts, partIndex, requestSegment, requestIndex, values);
        }

        RoutePatternParameterSegment parameter = (RoutePatternParameterSegment)currentPart;
        if (partIndex == parts.Count - 1)
        {
            return TryAssignParameter(parameter, requestSegment[requestIndex..], values);
        }

        string nextDelimiter = GetNextDelimiter(parts, partIndex + 1);
        for (int candidateIndex = requestSegment.Length - nextDelimiter.Length;
             candidateIndex >= requestIndex;
             candidateIndex--)
        {
            if (!requestSegment.AsSpan(candidateIndex).StartsWith(nextDelimiter.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RouteValueDictionary branchValues = new RouteValueDictionary(values);
            if (!TryAssignParameter(parameter, requestSegment[requestIndex..candidateIndex], branchValues))
            {
                continue;
            }

            if (TryMatchComplexSegment(parts, partIndex + 1, requestSegment, candidateIndex, branchValues))
            {
                CopyValues(branchValues, values);
                return true;
            }
        }

        if (partIndex == parts.Count - 3 &&
            parts[partIndex + 1] is RoutePatternSeparatorSegment &&
            parts[partIndex + 2] is RoutePatternParameterSegment trailingParameter)
        {
            RouteValueDictionary branchValues = new RouteValueDictionary(values);
            if (TryAssignParameter(parameter, requestSegment[requestIndex..], branchValues) &&
                TryAssignParameter(trailingParameter, string.Empty, branchValues))
            {
                CopyValues(branchValues, values);
                return true;
            }
        }

        return false;
    }

    private static bool TryConsumeText(
        string text,
        IReadOnlyList<RoutePatternSegment> parts,
        int partIndex,
        string requestSegment,
        int requestIndex,
        RouteValueDictionary values)
    {
        if (requestSegment.AsSpan(requestIndex).StartsWith(text.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return TryMatchComplexSegment(
                parts,
                partIndex + 1,
                requestSegment,
                requestIndex + text.Length,
                values);
        }

        return false;
    }

    private static bool TryMatchOmittedTrailingOptional(
        IReadOnlyList<RoutePatternSegment> parts,
        int partIndex,
        RouteValueDictionary values)
    {
        if (partIndex == parts.Count - 1 &&
            parts[partIndex] is RoutePatternParameterSegment parameter)
        {
            return TryAssignParameter(parameter, string.Empty, values);
        }

        if (partIndex == parts.Count - 2 &&
            parts[partIndex] is RoutePatternSeparatorSegment &&
            parts[partIndex + 1] is RoutePatternParameterSegment trailingParameter)
        {
            return TryAssignParameter(trailingParameter, string.Empty, values);
        }

        return false;
    }

    private static string GetNextDelimiter(IReadOnlyList<RoutePatternSegment> parts, int startIndex)
    {
        for (int index = startIndex; index < parts.Count; index++)
        {
            if (parts[index] is RoutePatternLiteralSegment literal)
            {
                return literal.Content;
            }

            if (parts[index] is RoutePatternSeparatorSegment separator)
            {
                return separator.Content;
            }
        }

        throw new InvalidOperationException("Complex route segments must contain a literal or separator delimiter after parameter parts.");
    }

    private static bool TryAssignParameter(
        RoutePatternParameterSegment parameter,
        string value,
        RouteValueDictionary values)
    {
        if (!string.IsNullOrEmpty(value))
        {
            values[parameter.Name] = value;
            return true;
        }

        if (parameter.Default is not null)
        {
            values[parameter.Name] = parameter.Default;
            return true;
        }

        if (parameter.IsOptional)
        {
            values.Remove(parameter.Name);
            return true;
        }

        return false;
    }

    private static void ApplyDefaults(RoutePattern pattern, RouteValueDictionary values)
    {
        foreach (KeyValuePair<string, object?> defaultValue in pattern.Defaults)
        {
            if (!values.ContainsKey(defaultValue.Key))
            {
                values[defaultValue.Key] = defaultValue.Value;
            }
        }
    }

    private static bool ValidatePolicies(Route route, IHttpContext httpContext, RouteValueDictionary values)
    {
        foreach (RoutePatternParameterSegment parameter in route.Pattern.Parameters)
        {
            if (!values.ContainsKey(parameter.Name))
            {
                // An omitted optional (or catch-all) parameter captured no value: its policies
                // constrain the value when one is present, they do not make the value required
                // (e.g. '{id:int?}' still matches when the id segment is absent).
                continue;
            }

            foreach (RoutePatternParameterPolicyReference policyReference in parameter.ParameterPolicies)
            {
                if (!route.PolicyMap.TryResolve(policyReference, out RouteParameterPolicy? policy) || policy is null)
                {
                    return false;
                }

                RouteParameterPolicyContext policyContext = new(httpContext, parameter, values);
                if (!policy.Applies(policyContext))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string[] SplitPath(HttpPath path)
    {
        string value = path.Value.Trim('/');
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<string>();
        }

        return value.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void CopyValues(RouteValueDictionary source, RouteValueDictionary destination)
    {
        destination.Clear();

        foreach (KeyValuePair<string, object?> pair in source)
        {
            destination.Add(pair.Key, pair.Value);
        }
    }

    private static void ValidatePatternPolicies(RoutePattern pattern, RouteParameterPolicyMap policyMap)
    {
        foreach (RoutePatternParameterSegment parameter in pattern.Parameters)
        {
            foreach (RoutePatternParameterPolicyReference reference in parameter.ParameterPolicies)
            {
                if (!policyMap.TryResolve(reference, out RouteParameterPolicy? policy) || policy is null)
                {
                    string policyText = reference.Content ?? reference.ParameterPolicy?.GetType().Name ?? "<unknown>";
                    throw new InvalidOperationException($"Route parameter '{parameter.Name}' references an unknown route policy '{policyText}'.");
                }
            }
        }
    }

    private sealed class EmptyRouterRouteHandler : IRouterRouteHandler
    {
        public Task InvokeAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
