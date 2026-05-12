using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

using Policies;

/// <summary>
/// Creates immutable route pattern components.
/// </summary>
public static class RoutePatternFactory
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyDictionary =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase));

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>> EmptyPoliciesDictionary =
        new ReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>>(
            new Dictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Creates a route pattern from the supplied path segments.
    /// </summary>
    /// <param name="rawText">The original route text, when available.</param>
    /// <param name="segments">The parsed path segments.</param>
    /// <returns>A normalized <see cref="RoutePattern"/>.</returns>
    public static RoutePattern Pattern(string? rawText, IEnumerable<RoutePatternPathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        return PatternCore(rawText, null, null, null, segments);
    }

    /// <summary>
    /// Creates a route pattern path segment from its individual parts.
    /// </summary>
    /// <param name="parts">The parts that form the segment.</param>
    /// <returns>A new <see cref="RoutePatternPathSegment"/>.</returns>
    public static RoutePatternPathSegment Segment(IEnumerable<RoutePatternSegment> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        return new RoutePatternPathSegment(parts.ToArray());
    }

    /// <summary>
    /// Creates a parameter route part.
    /// </summary>
    /// <param name="parameterName">The parameter name.</param>
    /// <param name="defaultValue">The optional default value.</param>
    /// <param name="parameterKind">The parameter kind.</param>
    /// <param name="parameterPolicies">The parameter policies.</param>
    /// <param name="encodeSlashes">Whether captured slashes should be encoded when generating URLs.</param>
    /// <returns>A new <see cref="RoutePatternParameterSegment"/>.</returns>
    public static RoutePatternParameterSegment ParameterPart(
        string parameterName,
        object? defaultValue,
        RoutePatternParameterKind parameterKind,
        IEnumerable<RoutePatternParameterPolicyReference>? parameterPolicies,
        bool encodeSlashes = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameterName);

        return ParameterPartCore(
            parameterName,
            defaultValue,
            parameterKind,
            parameterPolicies?.ToArray() ?? Array.Empty<RoutePatternParameterPolicyReference>(),
            encodeSlashes);
    }

    private static RoutePattern PatternCore(
        string? rawText,
        RouteValueDictionary? defaults,
        Dictionary<string, List<RoutePatternParameterPolicyReference>>? parameterPolicyReferences,
        RouteValueDictionary? requiredValues,
        IEnumerable<RoutePatternPathSegment> segments)
    {
        Dictionary<string, object?>? updatedDefaults = defaults is { Count: > 0 }
            ? new Dictionary<string, object?>(defaults, StringComparer.OrdinalIgnoreCase)
            : null;

        Dictionary<string, List<RoutePatternParameterPolicyReference>>? updatedParameterPolicies = parameterPolicyReferences is not null
            ? parameterPolicyReferences.ToDictionary(
                keySelector: pair => pair.Key,
                elementSelector: pair => new List<RoutePatternParameterPolicyReference>(pair.Value),
                comparer: StringComparer.OrdinalIgnoreCase)
            : null;

        RoutePatternPathSegment[] normalizedSegments = segments
            .Select(VisitSegment)
            .ToArray();

        RoutePatternParameterSegment[] parameters = normalizedSegments
            .SelectMany(segment => segment.Segments.OfType<RoutePatternParameterSegment>())
            .ToArray();

        ValidateRequiredValues(requiredValues, parameters, updatedDefaults);

        IReadOnlyDictionary<string, object?> normalizedDefaults = updatedDefaults is { Count: > 0 }
            ? new ReadOnlyDictionary<string, object?>(updatedDefaults)
            : EmptyDictionary;

        IReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>> normalizedPolicies = updatedParameterPolicies is { Count: > 0 }
            ? new ReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>>(
                updatedParameterPolicies.ToDictionary(
                    keySelector: pair => pair.Key,
                    elementSelector: pair => (IReadOnlyList<RoutePatternParameterPolicyReference>)pair.Value.ToArray(),
                    comparer: StringComparer.OrdinalIgnoreCase))
            : EmptyPoliciesDictionary;

        IReadOnlyDictionary<string, object?> normalizedRequiredValues = requiredValues is { Count: > 0 }
            ? new ReadOnlyDictionary<string, object?>(
                new Dictionary<string, object?>(requiredValues, StringComparer.OrdinalIgnoreCase))
            : EmptyDictionary;

        return new RoutePattern(
            rawText,
            normalizedDefaults,
            normalizedPolicies,
            normalizedRequiredValues,
            parameters,
            normalizedSegments);

        RoutePatternPathSegment VisitSegment(RoutePatternPathSegment segment)
        {
            RoutePatternSegment[]? rewrittenParts = null;

            for (int index = 0; index < segment.Segments.Count; index++)
            {
                RoutePatternSegment originalPart = segment.Segments[index];
                RoutePatternSegment rewrittenPart = VisitPart(originalPart);

                if (!ReferenceEquals(originalPart, rewrittenPart))
                {
                    rewrittenParts ??= segment.Segments.ToArray();
                    rewrittenParts[index] = rewrittenPart;
                }
            }

            return rewrittenParts is null
                ? segment
                : new RoutePatternPathSegment(rewrittenParts);
        }

        RoutePatternSegment VisitPart(RoutePatternSegment part)
        {
            if (part is not RoutePatternParameterSegment parameterPart)
            {
                return part;
            }

            object? effectiveDefault = parameterPart.Default;

            if (updatedDefaults is not null &&
                updatedDefaults.TryGetValue(parameterPart.Name, out object? explicitDefault))
            {
                if (parameterPart.Default is not null &&
                    !Equals(explicitDefault, parameterPart.Default))
                {
                    throw new InvalidOperationException(
                        $"Route parameter '{parameterPart.Name}' cannot define both an inline default and a conflicting explicit default value.");
                }

                if (parameterPart.IsOptional)
                {
                    throw new InvalidOperationException(
                        $"Route parameter '{parameterPart.Name}' cannot be optional and also define an explicit default value.");
                }

                effectiveDefault = explicitDefault;
            }

            if (parameterPart.Default is not null)
            {
                updatedDefaults ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                updatedDefaults[parameterPart.Name] = parameterPart.Default;
            }

            List<RoutePatternParameterPolicyReference>? policies = null;
            if (updatedParameterPolicies is not null)
            {
                updatedParameterPolicies.TryGetValue(parameterPart.Name, out policies);
            }

            if (parameterPart.ParameterPolicies.Count > 0)
            {
                updatedParameterPolicies ??= new Dictionary<string, List<RoutePatternParameterPolicyReference>>(StringComparer.OrdinalIgnoreCase);

                if (policies is null)
                {
                    policies = new List<RoutePatternParameterPolicyReference>(parameterPart.ParameterPolicies.Count);
                    updatedParameterPolicies[parameterPart.Name] = policies;
                }

                policies.AddRange(parameterPart.ParameterPolicies);
            }

            bool defaultsChanged = !Equals(parameterPart.Default, effectiveDefault);
            bool policiesChanged = policies is not null && policies.Count > 0;

            if (!defaultsChanged && !policiesChanged)
            {
                return part;
            }

            return ParameterPartCore(
                parameterPart.Name,
                effectiveDefault,
                parameterPart.ParameterKind,
                policies?.ToArray() ?? Array.Empty<RoutePatternParameterPolicyReference>(),
                parameterPart.EncodeSlashes);
        }
    }

    /// <summary>
    /// Creates a literal route part.
    /// </summary>
    /// <param name="content">The literal text.</param>
    /// <returns>A new <see cref="RoutePatternLiteralSegment"/>.</returns>
    public static RoutePatternLiteralSegment LiteralPart(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);

        if (content.Contains('?'))
        {
            throw new ArgumentException($"The literal route segment '{content}' is invalid.", nameof(content));
        }

        return LiteralPartCore(content);
    }

    private static RoutePatternLiteralSegment LiteralPartCore(string content)
    {
        return new RoutePatternLiteralSegment(content);
    }

    /// <summary>
    /// Creates a separator route part.
    /// </summary>
    /// <param name="content">The separator content.</param>
    /// <returns>A new <see cref="RoutePatternSeparatorSegment"/>.</returns>
    public static RoutePatternSeparatorSegment SeparatorPart(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);

        return SeparatorPartCore(content);
    }

    private static RoutePatternSeparatorSegment SeparatorPartCore(string content)
    {
        return new RoutePatternSeparatorSegment(content);
    }

    private static RoutePatternParameterSegment ParameterPartCore(
        string parameterName,
        object? defaultValue,
        RoutePatternParameterKind parameterKind,
        RoutePatternParameterPolicyReference[] parameterPolicies,
        bool encodeSlashes)
    {
        return new RoutePatternParameterSegment(parameterName, defaultValue, parameterKind, parameterPolicies, encodeSlashes);
    }

    /// <summary>
    /// Creates a parameter policy reference from a supported route constraint value.
    /// </summary>
    /// <param name="constraint">The constraint object or inline constraint text.</param>
    /// <returns>A parameter policy reference.</returns>
    public static RoutePatternParameterPolicyReference Constraint(object constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        if (constraint is RouteParameterPolicy parameterPolicy)
        {
            return ParameterPolicyCore(parameterPolicy);
        }

        if (constraint is string text)
        {
            return ParameterPolicyCore(new RegexRouteParameterPolicy($"^({text})$"));
        }

        throw new InvalidOperationException(
            $"The supplied route constraint reference '{constraint}' is not supported.");
    }

    /// <summary>
    /// Creates a parameter policy reference from a constraint instance.
    /// </summary>
    /// <param name="constraint">The constraint instance.</param>
    /// <returns>A parameter policy reference.</returns>
    public static RoutePatternParameterPolicyReference Constraint(RouteParameterPolicy constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        return ParameterPolicyCore(constraint);
    }

    /// <summary>
    /// Creates a parameter policy reference from inline constraint text.
    /// </summary>
    /// <param name="constraint">The inline constraint text.</param>
    /// <returns>A parameter policy reference.</returns>
    public static RoutePatternParameterPolicyReference Constraint(string constraint)
    {
        ArgumentException.ThrowIfNullOrEmpty(constraint);

        return ParameterPolicyCore(constraint);
    }

    /// <summary>
    /// Creates a parameter policy reference from a custom policy instance.
    /// </summary>
    /// <param name="parameterPolicy">The policy instance.</param>
    /// <returns>A parameter policy reference.</returns>
    public static RoutePatternParameterPolicyReference ParameterPolicy(RouteParameterPolicy parameterPolicy)
    {
        ArgumentNullException.ThrowIfNull(parameterPolicy);

        return ParameterPolicyCore(parameterPolicy);
    }

    /// <summary>
    /// Creates a parameter policy reference from inline policy text.
    /// </summary>
    /// <param name="parameterPolicy">The inline policy text.</param>
    /// <returns>A parameter policy reference.</returns>
    public static RoutePatternParameterPolicyReference ParameterPolicy(string parameterPolicy)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameterPolicy);

        return ParameterPolicyCore(parameterPolicy);
    }

    private static RoutePatternParameterPolicyReference ParameterPolicyCore(string parameterPolicy)
    {
        return new RoutePatternParameterPolicyReference(parameterPolicy);
    }

    private static RoutePatternParameterPolicyReference ParameterPolicyCore(RouteParameterPolicy parameterPolicy)
    {
        return new RoutePatternParameterPolicyReference(parameterPolicy);
    }

    private static void ValidateRequiredValues(
        RouteValueDictionary? requiredValues,
        IReadOnlyList<RoutePatternParameterSegment> parameters,
        IReadOnlyDictionary<string, object?>? defaults)
    {
        if (requiredValues is null)
        {
            return;
        }

        foreach (KeyValuePair<string, object?> requiredValue in requiredValues)
        {
            bool hasMatch = RouteValueEqualityComparer.Default.Equals(string.Empty, requiredValue.Value);

            if (!hasMatch)
            {
                hasMatch = parameters.Any(parameter =>
                    string.Equals(requiredValue.Key, parameter.Name, StringComparison.OrdinalIgnoreCase));
            }

            if (!hasMatch &&
                defaults is not null &&
                defaults.TryGetValue(requiredValue.Key, out object? defaultValue) &&
                RouteValueEqualityComparer.Default.Equals(requiredValue.Value, defaultValue))
            {
                hasMatch = true;
            }

            if (!hasMatch)
            {
                throw new InvalidOperationException(
                    $"No corresponding parameter or default value could be found for the required value '{requiredValue.Key}={requiredValue.Value}'.");
            }
        }
    }
}
