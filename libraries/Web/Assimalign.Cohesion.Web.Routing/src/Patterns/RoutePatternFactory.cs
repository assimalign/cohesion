using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Resources;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

using Internal;
using Constraints;

public static class RoutePatternFactory
{
    private static readonly IReadOnlyDictionary<string, object> EmptyDictionary = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>());

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>> EmptyPoliciesDictionary = new ReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>>(new Dictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>>());

    public static RoutePattern Pattern(string? rawText, IEnumerable<RoutePatternPathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments, "segments");
        return PatternCore(rawText, null, null, null, segments);
    }

    private static RoutePattern PatternCore(string rawText, RouteValueDictionary defaults, Dictionary<string, List<RoutePatternParameterPolicyReference>> parameterPolicyReferences, RouteValueDictionary requiredValues, IEnumerable<RoutePatternPathSegment> segments)
    {
        Dictionary<string, object> updatedDefaults = null;
        if (defaults != null && defaults.Count > 0)
        {
            updatedDefaults = new Dictionary<string, object>(defaults.Count, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object?> @default in defaults)
            {
                updatedDefaults.Add(@default.Key, @default.Value);
            }
        }
        List<RoutePatternParameterSegment> list = null;
        RoutePatternPathSegment[] array = segments.ToArray();
        for (int i = 0; i < array.Length; i++)
        {
            RoutePatternPathSegment routePatternPathSegment = (array[i] = VisitSegment(array[i]));
            for (int j = 0; j < routePatternPathSegment.Segments.Count; j++)
            {
                if (routePatternPathSegment.Segments[j] is RoutePatternParameterSegment item)
                {
                    if (list == null)
                    {
                        list = new List<RoutePatternParameterSegment>();
                    }
                    list.Add(item);
                }
            }
        }
        if (requiredValues != null)
        {
            foreach (KeyValuePair<string, object?> requiredValue in requiredValues)
            {
                bool flag = RouteValueEqualityComparer.Default.Equals(string.Empty, requiredValue.Value);
                if (!flag && list != null)
                {
                    for (int k = 0; k < list.Count; k++)
                    {
                        if (string.Equals(requiredValue.Key, list[k].Name, StringComparison.OrdinalIgnoreCase))
                        {
                            flag = true;
                            break;
                        }
                    }
                }
                if (!flag && updatedDefaults != null && updatedDefaults.TryGetValue(requiredValue.Key, out var value) && RouteValueEqualityComparer.Default.Equals(requiredValue.Value, value))
                {
                    flag = true;
                }
                if (!flag)
                {
                    throw new InvalidOperationException($"No corresponding parameter or default value could be found for the required value '{requiredValue.Key}={requiredValue.Value}'. A non-null required value must correspond to a route parameter or the route pattern must have a matching default value.");
                }
            }
        }
        IReadOnlyDictionary<string, object> readOnlyDictionary = updatedDefaults;
        IReadOnlyDictionary<string, object> defaults2 = readOnlyDictionary ?? EmptyDictionary;
        IReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>> parameterPolicies;
        if (parameterPolicyReferences == null)
        {
            parameterPolicies = EmptyPoliciesDictionary;
        }
        else
        {
            IReadOnlyDictionary<string, IReadOnlyList<RoutePatternParameterPolicyReference>> readOnlyDictionary2 = ((IEnumerable<KeyValuePair<string, List<RoutePatternParameterPolicyReference>>>)parameterPolicyReferences).ToDictionary((Func<KeyValuePair<string, List<RoutePatternParameterPolicyReference>>, string>)((KeyValuePair<string, List<RoutePatternParameterPolicyReference>> kvp) => kvp.Key), (Func<KeyValuePair<string, List<RoutePatternParameterPolicyReference>>, IReadOnlyList<RoutePatternParameterPolicyReference>>)((KeyValuePair<string, List<RoutePatternParameterPolicyReference>> kvp) => kvp.Value.ToArray()));
            parameterPolicies = readOnlyDictionary2;
        }
        readOnlyDictionary = requiredValues;
        IReadOnlyDictionary<string, object> requiredValues2 = readOnlyDictionary ?? EmptyDictionary;
        IReadOnlyList<RoutePatternParameterSegment> readOnlyList = list;
        return new RoutePattern(rawText, defaults2, parameterPolicies, requiredValues2, readOnlyList ?? Array.Empty<RoutePatternParameterSegment>(), array);
        RoutePatternSegment VisitPart(RoutePatternSegment part)
        {
            if (!part.IsParameter)
            {
                return part;
            }
            RoutePatternParameterSegment routePatternParameterPart = (RoutePatternParameterSegment)part;
            object obj = routePatternParameterPart.Default;
            if (updatedDefaults != null && updatedDefaults.TryGetValue(routePatternParameterPart.Name, out var value2))
            {
                if (routePatternParameterPart.Default != null && !object.Equals(value2, routePatternParameterPart.Default))
                {
                    throw new InvalidOperationException("");// Resources.FormatTemplateRoute_CannotHaveDefaultValueSpecifiedInlineAndExplicitly(routePatternParameterPart.Name));
                }
                if (routePatternParameterPart.IsOptional)
                {
                    throw new InvalidOperationException("");// Resources.TemplateRoute_OptionalCannotHaveDefaultValue);
                }
                obj = value2;
            }
            if (routePatternParameterPart.Default != null)
            {
                if (updatedDefaults == null)
                {
                    updatedDefaults = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }
                updatedDefaults[routePatternParameterPart.Name] = routePatternParameterPart.Default;
            }
            List<RoutePatternParameterPolicyReference> value3 = null;
            if ((parameterPolicyReferences == null || !parameterPolicyReferences.TryGetValue(routePatternParameterPart.Name, out value3)) && routePatternParameterPart.ParameterPolicies.Count > 0)
            {
                if (parameterPolicyReferences == null)
                {
                    parameterPolicyReferences = new Dictionary<string, List<RoutePatternParameterPolicyReference>>(StringComparer.OrdinalIgnoreCase);
                }
                value3 = new List<RoutePatternParameterPolicyReference>(routePatternParameterPart.ParameterPolicies.Count);
                parameterPolicyReferences.Add(routePatternParameterPart.Name, value3);
            }
            if (routePatternParameterPart.ParameterPolicies.Count > 0)
            {
                value3.AddRange(routePatternParameterPart.ParameterPolicies);
            }
            if (object.Equals(routePatternParameterPart.Default, obj) && routePatternParameterPart.ParameterPolicies.Count == 0 && (value3 == null || value3.Count == 0))
            {
                return part;
            }
            return ParameterPartCore(routePatternParameterPart.Name, obj, routePatternParameterPart.ParameterKind, value3?.ToArray() ?? Array.Empty<RoutePatternParameterPolicyReference>(), routePatternParameterPart.EncodeSlashes);
        }
        RoutePatternPathSegment VisitSegment(RoutePatternPathSegment segment)
        {
            RoutePatternSegment[] array2 = null;
            for (int l = 0; l < segment.Segments.Count; l++)
            {
                RoutePatternSegment routePatternPart = segment.Segments[l];
                RoutePatternSegment routePatternPart2 = VisitPart(routePatternPart);
                if (routePatternPart != routePatternPart2)
                {
                    if (array2 == null)
                    {
                        array2 = segment.Segments.ToArray();
                    }
                    array2[l] = routePatternPart2;
                }
            }
            if (array2 == null)
            {
                return segment;
            }
            return new RoutePatternPathSegment(array2);
        }
    }

    public static RoutePatternLiteralSegment LiteralPart(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content, "content");
        if (content.Contains('?'))
        {
            throw new ArgumentException("");// Resources.FormatTemplateRoute_InvalidLiteral(content));
        }
        return LiteralPartCore(content);
    }

    private static RoutePatternLiteralSegment LiteralPartCore(string content)
    {
        return new RoutePatternLiteralSegment(content);
    }

    public static RoutePatternSeparatorSegment SeparatorPart(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content, "content");
        return SeparatorPartCore(content);
    }

    private static RoutePatternSeparatorSegment SeparatorPartCore(string content)
    {
        return new RoutePatternSeparatorSegment(content);
    }

    private static RoutePatternParameterSegment ParameterPartCore(string parameterName, object @default, RoutePatternParameterKind parameterKind, RoutePatternParameterPolicyReference[] parameterPolicies, bool encodeSlashes)
    {
        return new RoutePatternParameterSegment(parameterName, @default, parameterKind, parameterPolicies, encodeSlashes);
    }

    public static RoutePatternParameterPolicyReference Constraint(object constraint)
    {
        if (constraint is IRouteParameterConstraintPolicy parameterPolicy)
        {
            return ParameterPolicyCore(parameterPolicy);
        }
        if (constraint is string text)
        {
            return ParameterPolicyCore(new RegexRouteConstraint("^(" + text + ")$"));
        }
        throw new InvalidOperationException("");// Resources.FormatRoutePattern_InvalidConstraintReference(constraint ?? "null", typeof(IRouteConstraint)));
    }

    public static RoutePatternParameterPolicyReference Constraint(IRouteParameterConstraintPolicy constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint, "constraint");
        return ParameterPolicyCore(constraint);
    }

    public static RoutePatternParameterPolicyReference Constraint(string constraint)
    {
        ArgumentException.ThrowIfNullOrEmpty(constraint, "constraint");
        return ParameterPolicyCore(constraint);
    }

    public static RoutePatternParameterPolicyReference ParameterPolicy(IRouteParameterPolicy parameterPolicy)
    {
        ArgumentNullException.ThrowIfNull(parameterPolicy, "parameterPolicy");
        return ParameterPolicyCore(parameterPolicy);
    }

    public static RoutePatternParameterPolicyReference ParameterPolicy(string parameterPolicy)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameterPolicy, "parameterPolicy");
        return ParameterPolicyCore(parameterPolicy);
    }

    private static RoutePatternParameterPolicyReference ParameterPolicyCore(string parameterPolicy)
    {
        return new RoutePatternParameterPolicyReference(parameterPolicy);
    }

    private static RoutePatternParameterPolicyReference ParameterPolicyCore(IRouteParameterPolicy parameterPolicy)
    {
        return new RoutePatternParameterPolicyReference(parameterPolicy);
    }
}