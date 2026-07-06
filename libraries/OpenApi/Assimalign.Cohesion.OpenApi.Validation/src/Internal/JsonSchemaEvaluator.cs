using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.OpenApi.Validation;

/// <summary>
/// A violation reported by <see cref="JsonSchemaEvaluator"/>: the JSON Pointer of the offending
/// instance location and a human-readable message.
/// </summary>
internal readonly struct JsonSchemaViolation(string pointer, string message)
{
    public string Pointer { get; } = pointer;

    public string Message { get; } = message;
}

/// <summary>
/// Evaluates JSON instances against the vendored official OpenAPI meta-schemas. Supports the keyword
/// subset those schemas actually use, across both dialects: draft-04 (the 3.0 schema) and draft
/// 2020-12 (the 3.1/3.2 schemas), including <c>$defs</c>/<c>definitions</c> references,
/// <c>$dynamicRef</c> (resolved to the first matching anchor in document order — sufficient for the
/// OAS meta-schemas, whose dynamic scope starts at the root), applicators, and
/// <c>unevaluatedProperties</c> with annotation tracking.
/// </summary>
internal sealed class JsonSchemaEvaluator
{
    private readonly JsonElement _root;
    private readonly Dictionary<string, JsonElement> _anchors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    internal JsonSchemaEvaluator(JsonDocument schemaDocument)
    {
        _root = schemaDocument.RootElement;
        IndexAnchors(_root);
    }

    internal List<JsonSchemaViolation> Validate(JsonElement instance)
    {
        var violations = new List<JsonSchemaViolation>();
        Evaluate(_root, instance, "#", violations, out _);
        return violations;
    }

    /// <summary>
    /// Evaluates <paramref name="instance"/> against <paramref name="schema"/>. When
    /// <paramref name="violations"/> is <see langword="null"/> the evaluation is a silent probe (used
    /// for applicator branches). Evaluated property names are surfaced for
    /// <c>unevaluatedProperties</c> only when the schema succeeds.
    /// </summary>
    private bool Evaluate(JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation>? violations, out HashSet<string>? evaluatedProperties)
    {
        evaluatedProperties = null;

        if (schema.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (schema.ValueKind == JsonValueKind.False)
        {
            Report(violations, pointer, "is not allowed here");
            return false;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        var valid = true;
        var evaluated = new HashSet<string>(StringComparer.Ordinal);

        // References may sit alongside other keywords (2020-12); in the draft-04 schema they stand alone.
        if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
        {
            valid &= EvaluateReference(reference.GetString()!, instance, pointer, violations, evaluated);
        }

        if (schema.TryGetProperty("$dynamicRef", out var dynamicReference) && dynamicReference.ValueKind == JsonValueKind.String)
        {
            valid &= EvaluateReference(dynamicReference.GetString()!, instance, pointer, violations, evaluated);
        }

        valid &= EvaluateType(schema, instance, pointer, violations);
        valid &= EvaluateConstants(schema, instance, pointer, violations);
        valid &= EvaluateApplicators(schema, instance, pointer, violations, evaluated);

        if (instance.ValueKind == JsonValueKind.Object)
        {
            valid &= EvaluateObject(schema, instance, pointer, violations, evaluated);
        }
        else if (instance.ValueKind == JsonValueKind.Array)
        {
            valid &= EvaluateArray(schema, instance, pointer, violations);
        }
        else if (instance.ValueKind == JsonValueKind.String)
        {
            valid &= EvaluateString(schema, instance, pointer, violations);
        }
        else if (instance.ValueKind == JsonValueKind.Number)
        {
            valid &= EvaluateNumber(schema, instance, pointer, violations);
        }

        // unevaluatedProperties runs last, over everything the other keywords accounted for.
        if (valid && instance.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("unevaluatedProperties", out var unevaluated))
        {
            foreach (var property in instance.EnumerateObject())
            {
                if (evaluated.Contains(property.Name))
                {
                    continue;
                }

                if (!Evaluate(unevaluated, property.Value, Append(pointer, property.Name), null, out _))
                {
                    Report(violations, Append(pointer, property.Name), $"property '{property.Name}' is not defined for this object");
                    valid = false;
                }
                else
                {
                    evaluated.Add(property.Name);
                }
            }
        }

        if (valid)
        {
            evaluatedProperties = evaluated;
        }

        return valid;
    }

    private bool EvaluateReference(string reference, JsonElement instance, string pointer, List<JsonSchemaViolation>? violations, HashSet<string> evaluated)
    {
        if (!TryResolve(reference, out var target))
        {
            // External or unknown references are treated as satisfied; the vendored schemas are
            // self-contained, so this only affects the pinned-dialect indirection.
            return true;
        }

        var valid = Evaluate(target, instance, pointer, violations, out var referenced);
        if (referenced is not null)
        {
            evaluated.UnionWith(referenced);
        }

        return valid;
    }

    private bool TryResolve(string reference, out JsonElement target)
    {
        target = default;

        if (reference.Length == 0 || reference[0] != '#')
        {
            return false;
        }

        if (reference.Length == 1)
        {
            target = _root;
            return true;
        }

        if (reference[1] != '/')
        {
            return _anchors.TryGetValue(reference[1..], out target);
        }

        var current = _root;
        foreach (var segment in reference[2..].Split('/'))
        {
            var name = segment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return false;
            }
        }

        target = current;
        return true;
    }

    private static bool EvaluateType(JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation>? violations)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return true;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            if (MatchesType(type.GetString()!, instance))
            {
                return true;
            }

            Report(violations, pointer, $"expected type '{type.GetString()}'");
            return false;
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in type.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.String && MatchesType(candidate.GetString()!, instance))
                {
                    return true;
                }
            }

            Report(violations, pointer, "does not match any of the expected types");
            return false;
        }

        return true;
    }

    private static bool MatchesType(string type, JsonElement instance) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        "number" => instance.ValueKind == JsonValueKind.Number,
        "integer" => instance.ValueKind == JsonValueKind.Number
            && (instance.TryGetInt64(out _) || (instance.TryGetDouble(out var value) && double.IsInteger(value))),
        _ => true
    };

    private static bool EvaluateConstants(JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation>? violations)
    {
        var valid = true;

        if (schema.TryGetProperty("enum", out var enumeration) && enumeration.ValueKind == JsonValueKind.Array)
        {
            var matched = false;
            foreach (var candidate in enumeration.EnumerateArray())
            {
                if (JsonElement.DeepEquals(candidate, instance))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                Report(violations, pointer, "is not one of the allowed values");
                valid = false;
            }
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, instance))
        {
            Report(violations, pointer, "does not equal the required constant value");
            valid = false;
        }

        return valid;
    }

    private bool EvaluateApplicators(JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation>? violations, HashSet<string> evaluated)
    {
        var valid = true;

        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var subschema in allOf.EnumerateArray())
            {
                valid &= Evaluate(subschema, instance, pointer, violations, out var branch);
                Merge(evaluated, branch);
            }
        }

        if (schema.TryGetProperty("anyOf", out var anyOf) && anyOf.ValueKind == JsonValueKind.Array)
        {
            var any = false;
            foreach (var subschema in anyOf.EnumerateArray())
            {
                if (Evaluate(subschema, instance, pointer, null, out var branch))
                {
                    any = true;
                    Merge(evaluated, branch);
                }
            }

            if (!any)
            {
                Report(violations, pointer, "does not match any of the expected schemas");
                valid = false;
            }
        }

        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            var matches = 0;
            foreach (var subschema in oneOf.EnumerateArray())
            {
                if (Evaluate(subschema, instance, pointer, null, out var branch))
                {
                    matches++;
                    Merge(evaluated, branch);
                }
            }

            if (matches != 1)
            {
                Report(violations, pointer, matches == 0 ? "does not match any of the expected schemas" : "matches more than one exclusive schema");
                valid = false;
            }
        }

        if (schema.TryGetProperty("not", out var not) && Evaluate(not, instance, pointer, null, out _))
        {
            Report(violations, pointer, "matches a schema it must not match");
            valid = false;
        }

        if (schema.TryGetProperty("if", out var condition))
        {
            if (Evaluate(condition, instance, pointer, null, out var conditionEvaluated))
            {
                Merge(evaluated, conditionEvaluated);
                if (schema.TryGetProperty("then", out var then))
                {
                    valid &= Evaluate(then, instance, pointer, violations, out var branch);
                    Merge(evaluated, branch);
                }
            }
            else if (schema.TryGetProperty("else", out var otherwise))
            {
                valid &= Evaluate(otherwise, instance, pointer, violations, out var branch);
                Merge(evaluated, branch);
            }
        }

        if (instance.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("dependentSchemas", out var dependents) && dependents.ValueKind == JsonValueKind.Object)
        {
            foreach (var dependent in dependents.EnumerateObject())
            {
                if (instance.TryGetProperty(dependent.Name, out _))
                {
                    valid &= Evaluate(dependent.Value, instance, pointer, violations, out var branch);
                    Merge(evaluated, branch);
                }
            }
        }

        return valid;
    }

    private bool EvaluateObject(JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation>? violations, HashSet<string> evaluated)
    {
        var valid = true;

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray())
            {
                if (name.ValueKind == JsonValueKind.String && !instance.TryGetProperty(name.GetString()!, out _))
                {
                    Report(violations, pointer, $"is missing the required property '{name.GetString()}'");
                    valid = false;
                }
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        var hasPatterns = schema.TryGetProperty("patternProperties", out var patternProperties) && patternProperties.ValueKind == JsonValueKind.Object;
        var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);

        foreach (var property in instance.EnumerateObject())
        {
            var matched = false;

            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                valid &= Evaluate(propertySchema, property.Value, Append(pointer, property.Name), violations, out _);
                matched = true;
            }

            if (hasPatterns)
            {
                foreach (var pattern in patternProperties.EnumerateObject())
                {
                    if (GetPattern(pattern.Name).IsMatch(property.Name))
                    {
                        valid &= Evaluate(pattern.Value, property.Value, Append(pointer, property.Name), violations, out _);
                        matched = true;
                    }
                }
            }

            if (matched)
            {
                evaluated.Add(property.Name);
            }
            else if (hasAdditional)
            {
                if (!Evaluate(additional, property.Value, Append(pointer, property.Name), null, out _))
                {
                    Report(violations, Append(pointer, property.Name), $"property '{property.Name}' is not defined for this object");
                    valid = false;
                }
                else
                {
                    evaluated.Add(property.Name);
                }
            }
        }

        if (schema.TryGetProperty("propertyNames", out var propertyNames))
        {
            foreach (var property in instance.EnumerateObject())
            {
                using var name = JsonDocument.Parse($"\"{JsonEncodedText.Encode(property.Name)}\"");
                if (!Evaluate(propertyNames, name.RootElement, Append(pointer, property.Name), null, out _))
                {
                    Report(violations, Append(pointer, property.Name), $"property name '{property.Name}' is not allowed");
                    valid = false;
                }
            }
        }

        var hasMin = schema.TryGetProperty("minProperties", out var minProperties) && minProperties.TryGetInt32(out _);
        var hasMax = schema.TryGetProperty("maxProperties", out var maxProperties) && maxProperties.TryGetInt32(out _);
        if (hasMin || hasMax)
        {
            var count = 0;
            foreach (var _ in instance.EnumerateObject())
            {
                count++;
            }

            if (hasMin && count < minProperties.GetInt32())
            {
                Report(violations, pointer, $"must have at least {minProperties.GetInt32()} properties");
                valid = false;
            }

            if (hasMax && count > maxProperties.GetInt32())
            {
                Report(violations, pointer, $"must have at most {maxProperties.GetInt32()} properties");
                valid = false;
            }
        }

        return valid;
    }

    private bool EvaluateArray(JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation>? violations)
    {
        var valid = true;
        var length = instance.GetArrayLength();
        var prefixLength = 0;

        if (schema.TryGetProperty("prefixItems", out var prefixItems) && prefixItems.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var subschema in prefixItems.EnumerateArray())
            {
                if (index >= length)
                {
                    break;
                }

                valid &= Evaluate(subschema, instance[index], Append(pointer, index.ToString()), violations, out _);
                index++;
            }

            prefixLength = index;
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False)
        {
            for (var index = prefixLength; index < length; index++)
            {
                valid &= Evaluate(items, instance[index], Append(pointer, index.ToString()), violations, out _);
            }
        }

        // contains / minContains / maxContains: count the items matching the contained schema.
        if (schema.TryGetProperty("contains", out var contains))
        {
            var matches = 0;
            for (var index = 0; index < length; index++)
            {
                if (Evaluate(contains, instance[index], Append(pointer, index.ToString()), null, out _))
                {
                    matches++;
                }
            }

            var minContains = schema.TryGetProperty("minContains", out var min) && min.TryGetInt32(out var minValue) ? minValue : 1;
            if (matches < minContains)
            {
                Report(violations, pointer, $"must contain at least {minContains} matching item(s)");
                valid = false;
            }

            if (schema.TryGetProperty("maxContains", out var max) && max.TryGetInt32(out var maxValue) && matches > maxValue)
            {
                Report(violations, pointer, $"must contain at most {maxValue} matching item(s)");
                valid = false;
            }
        }

        if (schema.TryGetProperty("minItems", out var minItems) && minItems.TryGetInt32(out var minimum) && length < minimum)
        {
            Report(violations, pointer, $"must have at least {minimum} items");
            valid = false;
        }

        if (schema.TryGetProperty("maxItems", out var maxItems) && maxItems.TryGetInt32(out var maximum) && length > maximum)
        {
            Report(violations, pointer, $"must have at most {maximum} items");
            valid = false;
        }

        if (schema.TryGetProperty("uniqueItems", out var uniqueItems) && uniqueItems.ValueKind == JsonValueKind.True)
        {
            for (var first = 0; first < length && valid; first++)
            {
                for (var second = first + 1; second < length; second++)
                {
                    if (JsonElement.DeepEquals(instance[first], instance[second]))
                    {
                        Report(violations, pointer, "must not contain duplicate items");
                        valid = false;
                        break;
                    }
                }
            }
        }

        return valid;
    }

    private bool EvaluateString(JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation>? violations)
    {
        var valid = true;
        var value = instance.GetString()!;

        if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String
            && !GetPattern(pattern.GetString()!).IsMatch(value))
        {
            Report(violations, pointer, "does not match the required pattern");
            valid = false;
        }

        if (schema.TryGetProperty("minLength", out var minLength) && minLength.TryGetInt32(out var minimum)
            && CountCodePoints(value) < minimum)
        {
            Report(violations, pointer, $"must be at least {minimum} characters long");
            valid = false;
        }

        if (schema.TryGetProperty("maxLength", out var maxLength) && maxLength.TryGetInt32(out var maximum)
            && CountCodePoints(value) > maximum)
        {
            Report(violations, pointer, $"must be at most {maximum} characters long");
            valid = false;
        }

        return valid;
    }

    private static int CountCodePoints(string value)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsLowSurrogate(value[index]))
            {
                count++;
            }
        }

        return count;
    }

    private static bool EvaluateNumber(JsonElement schema, JsonElement instance, string pointer, List<JsonSchemaViolation>? violations)
    {
        var valid = true;
        var value = instance.GetDouble();

        if (schema.TryGetProperty("minimum", out var minimum) && minimum.ValueKind == JsonValueKind.Number)
        {
            // draft-04 uses a boolean exclusiveMinimum flag paired with minimum; 2020-12 uses a number.
            var exclusive = schema.TryGetProperty("exclusiveMinimum", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (exclusive ? value <= minimum.GetDouble() : value < minimum.GetDouble())
            {
                Report(violations, pointer, "is below the allowed minimum");
                valid = false;
            }
        }

        if (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) && exclusiveMinimum.ValueKind == JsonValueKind.Number
            && value <= exclusiveMinimum.GetDouble())
        {
            Report(violations, pointer, "is below the allowed exclusive minimum");
            valid = false;
        }

        if (schema.TryGetProperty("maximum", out var maximum) && maximum.ValueKind == JsonValueKind.Number)
        {
            var exclusive = schema.TryGetProperty("exclusiveMaximum", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (exclusive ? value >= maximum.GetDouble() : value > maximum.GetDouble())
            {
                Report(violations, pointer, "is above the allowed maximum");
                valid = false;
            }
        }

        if (schema.TryGetProperty("exclusiveMaximum", out var exclusiveMaximum) && exclusiveMaximum.ValueKind == JsonValueKind.Number
            && value >= exclusiveMaximum.GetDouble())
        {
            Report(violations, pointer, "is above the allowed exclusive maximum");
            valid = false;
        }

        if (schema.TryGetProperty("multipleOf", out var multipleOf) && multipleOf.ValueKind == JsonValueKind.Number)
        {
            var divisor = multipleOf.GetDouble();
            if (divisor != 0)
            {
                var quotient = value / divisor;
                if (Math.Abs(quotient - Math.Round(quotient)) > 1e-9)
                {
                    Report(violations, pointer, $"is not a multiple of {divisor}");
                    valid = false;
                }
            }
        }

        return valid;
    }

    private Regex GetPattern(string pattern)
    {
        if (!_patterns.TryGetValue(pattern, out var regex))
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            _patterns[pattern] = regex;
        }

        return regex;
    }

    private void IndexAnchors(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "$anchor" or "$dynamicAnchor" && property.Value.ValueKind == JsonValueKind.String)
                {
                    _anchors.TryAdd(property.Value.GetString()!, element);
                }

                IndexAnchors(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                IndexAnchors(item);
            }
        }
    }

    private static void Merge(HashSet<string> target, HashSet<string>? source)
    {
        if (source is not null)
        {
            target.UnionWith(source);
        }
    }

    private static string Append(string pointer, string segment) =>
        pointer + "/" + segment.Replace("~", "~0").Replace("/", "~1");

    private static void Report(List<JsonSchemaViolation>? violations, string pointer, string message) =>
        violations?.Add(new JsonSchemaViolation(pointer, message));
}
