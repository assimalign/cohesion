using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

[DebuggerDisplay("{DebuggerToString()}")]
public sealed class RoutePattern
{
    private const string SeparatorString = "/";

    public RoutePattern(
        string? rawText,
        IReadOnlyDictionary<string, object?> defaults,
        IReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>> parameterPolicies,
        IReadOnlyDictionary<string, object?> requiredValues,
        IReadOnlyList<RoutePatternParameterSegment> parameters,
        IReadOnlyList<RoutePatternPathSegment> pathSegments)
    {
        Debug.Assert(defaults != null);
        Debug.Assert(parameterPolicies != null);
        Debug.Assert(parameters != null);
        Debug.Assert(requiredValues != null);
        Debug.Assert(pathSegments != null);

        RawText = rawText;
        Defaults = defaults;
        ParameterPolicies = parameterPolicies;
        RequiredValues = requiredValues;
        Parameters = parameters;
        PathSegments = pathSegments;

        InboundPrecedence = RoutePrecedence.ComputeInbound(this);
        OutboundPrecedence = RoutePrecedence.ComputeOutbound(this);
    }

    /// <summary>
    /// A marker object that can be used in <see cref="RequiredValues"/> to designate that
    /// any non-null or non-empty value is required.
    /// </summary>
    /// <remarks>
    /// <see cref="RequiredValueAny"/> is only use in routing is in <see cref="RoutePattern.RequiredValues"/>.
    /// <see cref="RequiredValueAny"/> is not valid as a route value, and will convert to the null/empty string.
    /// </remarks>
    public static readonly object RequiredValueAny = new RequiredValueAnySentinal();

    /// <summary>
    /// Gets the set of default values for the route pattern.
    /// The keys of <see cref="Defaults"/> are the route parameter names.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Defaults { get; }

    /// <summary>
    /// Gets the set of parameter policy references for the route pattern.
    /// The keys of <see cref="ParameterPolicies"/> are the route parameter names.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>> ParameterPolicies { get; }

    /// <summary>
    /// Gets a collection of route values that must be provided for this route pattern to be considered
    /// applicable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RequiredValues"/> allows a framework to substitute route values into a parameterized template
    /// so that the same route template specification can be used to create multiple route patterns.
    /// <example>
    /// This example shows how a route template can be used with required values to substitute known
    /// route values for parameters.
    /// <code>
    /// Route Template: "{controller=Home}/{action=Index}/{id?}"
    /// Route Values: { controller = "Store", action = "Index" }
    /// </code>
    ///
    /// A route pattern produced in this way will match and generate URL paths like: <c>/Store</c>,
    /// <c>/Store/Index</c>, and <c>/Store/Index/17</c>.
    /// </example>
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, object?> RequiredValues { get; }

    /// <summary>
    /// Gets the precedence value of the route pattern for URL matching.
    /// </summary>
    /// <remarks>
    /// Precedence is a computed value based on the structure of the route pattern
    /// used for building URL matching data structures.
    /// </remarks>
    public decimal InboundPrecedence { get; }

    /// <summary>
    /// Gets the precedence value of the route pattern for URL generation.
    /// </summary>
    /// <remarks>
    /// Precedence is a computed value based on the structure of the route pattern
    /// used for building URL generation data structures.
    /// </remarks>
    public decimal OutboundPrecedence { get; }

    /// <summary>
    /// Gets the raw text supplied when parsing the route pattern. May be null.
    /// </summary>
    public string? RawText { get; }

    /// <summary>
    /// Gets the list of route parameters.
    /// </summary>
    public IReadOnlyList<RoutePatternParameterSegment> Parameters { get; }

    /// <summary>
    /// Gets the list of path segments.
    /// </summary>
    public IReadOnlyList<RoutePatternPathSegment> PathSegments { get; }

    /// <summary>
    /// Gets the parameter matching the given name.
    /// </summary>
    /// <param name="name">The name of the parameter to match.</param>
    /// <returns>The matching parameter or <c>null</c> if no parameter matches the given name.</returns>
    public RoutePatternParameterSegment? GetParameter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var parameters = Parameters;
        // Read interface .Count once rather than per iteration
        var parametersCount = parameters.Count;
        for (var i = 0; i < parametersCount; i++)
        {
            var parameter = parameters[i];
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter;
            }
        }

        return null;
    }

    // Used for:
    // 1. RoutePattern debug string.
    // 2. Default IRouteDiagnosticsMetadata value.
    // 3. RouteEndpoint display name.
    internal string DebuggerToString()
    {
        return RawText ?? string.Join(SeparatorString, PathSegments.Select(s => s.DebuggerToString()));
    }

    internal static bool IsRequiredValueAny(object? value)
    {
        return object.ReferenceEquals(RequiredValueAny, value);
    }

    [DebuggerDisplay("{DebuggerToString(),nq}")]
    private sealed class RequiredValueAnySentinal
    {
        private static string DebuggerToString() => "*any*";
    }
}
