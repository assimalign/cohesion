using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;

internal static class FocusedJsonSchemaValidator
{
    public static void Validate(JsonElement instance, JsonElement schema)
    {
        Validate(instance, schema, schema, "$", new HashSet<string>(StringComparer.Ordinal));
    }

    private static void Validate(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        HashSet<string> references)
    {
        if (schema.TryGetProperty("$ref", out JsonElement referenceElement))
        {
            string reference = referenceElement.GetString()
                ?? throw new InvalidOperationException($"Schema reference at '{path}' is null.");
            if (!references.Add(reference))
            {
                return;
            }

            Validate(instance, ResolveReference(rootSchema, reference), rootSchema, path, references);
            references.Remove(reference);
        }

        if (schema.TryGetProperty("allOf", out JsonElement allOf))
        {
            foreach (JsonElement branch in allOf.EnumerateArray())
            {
                Validate(instance, branch, rootSchema, path, references);
            }
        }

        ValidateAlternatives(instance, schema, rootSchema, path, references, "anyOf", requireExactlyOne: false);
        ValidateAlternatives(instance, schema, rootSchema, path, references, "oneOf", requireExactlyOne: true);

        if (schema.TryGetProperty("if", out JsonElement condition) &&
            Matches(instance, condition, rootSchema, path, references))
        {
            if (schema.TryGetProperty("then", out JsonElement consequent))
            {
                Validate(instance, consequent, rootSchema, path, references);
            }
        }
        else if (schema.TryGetProperty("else", out JsonElement alternative))
        {
            Validate(instance, alternative, rootSchema, path, references);
        }

        if (schema.TryGetProperty("type", out JsonElement typeElement) && !MatchesType(instance, typeElement))
        {
            throw new InvalidOperationException(
                $"Manifest value at '{path}' has JSON type '{instance.ValueKind}' but the schema requires {typeElement.GetRawText()}.");
        }

        if (schema.TryGetProperty("enum", out JsonElement enumElement) &&
            !enumElement.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, instance)))
        {
            throw new InvalidOperationException(
                $"Manifest value at '{path}' is {instance.GetRawText()}, which is not in schema enum {enumElement.GetRawText()}.");
        }

        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonElement.DeepEquals(constant, instance))
        {
            throw new InvalidOperationException(
                $"Manifest value at '{path}' is {instance.GetRawText()}, not schema constant {constant.GetRawText()}.");
        }

        ValidateScalarConstraints(instance, schema, path);

        if (instance.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(instance, schema, rootSchema, path, references);
        }
        else if (instance.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("minItems", out JsonElement minimumItems) &&
                instance.GetArrayLength() < minimumItems.GetInt32())
            {
                throw new InvalidOperationException(
                    $"Manifest array at '{path}' has fewer than {minimumItems.GetInt32()} items.");
            }

            if (schema.TryGetProperty("items", out JsonElement itemsSchema))
            {
                int index = 0;
                foreach (JsonElement item in instance.EnumerateArray())
                {
                    Validate(item, itemsSchema, rootSchema, $"{path}[{index}]", references);
                    index++;
                }
            }
        }
    }

    private static bool Matches(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        HashSet<string> references)
    {
        try
        {
            Validate(instance, schema, rootSchema, path, new HashSet<string>(references, StringComparer.Ordinal));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void ValidateAlternatives(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        HashSet<string> references,
        string keyword,
        bool requireExactlyOne)
    {
        if (!schema.TryGetProperty(keyword, out JsonElement alternatives))
        {
            return;
        }

        int matches = 0;
        var errors = new List<string>();
        foreach (JsonElement branch in alternatives.EnumerateArray())
        {
            try
            {
                Validate(instance, branch, rootSchema, path, new HashSet<string>(references, StringComparer.Ordinal));
                matches++;
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(exception.Message);
            }
        }

        if (matches == 0 || (requireExactlyOne && matches != 1))
        {
            throw new InvalidOperationException(
                $"Manifest value at '{path}' does not satisfy schema keyword '{keyword}'. {string.Join(" ", errors)}");
        }
    }

    private static void ValidateObject(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        HashSet<string> references)
    {
        if (schema.TryGetProperty("required", out JsonElement requiredElement))
        {
            foreach (JsonElement requiredName in requiredElement.EnumerateArray())
            {
                string propertyName = requiredName.GetString()
                    ?? throw new InvalidOperationException($"Schema required entry at '{path}' is null.");
                if (!instance.TryGetProperty(propertyName, out _))
                {
                    throw new InvalidOperationException(
                        $"Manifest object at '{path}' is missing schema-required property '{propertyName}'.");
                }
            }
        }

        var knownProperties = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out JsonElement propertiesElement))
        {
            foreach (JsonProperty propertySchema in propertiesElement.EnumerateObject())
            {
                knownProperties.Add(propertySchema.Name);
                if (instance.TryGetProperty(propertySchema.Name, out JsonElement propertyValue))
                {
                    Validate(propertyValue, propertySchema.Value, rootSchema, $"{path}.{propertySchema.Name}", references);
                }
            }
        }

        foreach (JsonProperty property in instance.EnumerateObject())
        {
            if (schema.TryGetProperty("propertyNames", out JsonElement propertyNameSchema))
            {
                using JsonDocument propertyNameDocument = JsonDocument.Parse(
                    $"\"{JsonEncodedText.Encode(property.Name)}\"");
                Validate(
                    propertyNameDocument.RootElement,
                    propertyNameSchema,
                    rootSchema,
                    $"{path}.<propertyName>",
                    references);
            }

            if (knownProperties.Contains(property.Name) || !schema.TryGetProperty("additionalProperties", out JsonElement additional))
            {
                continue;
            }

            if (additional.ValueKind == JsonValueKind.False)
            {
                throw new InvalidOperationException(
                    $"Manifest object at '{path}' contains schema-forbidden property '{property.Name}'.");
            }
            if (additional.ValueKind == JsonValueKind.Object)
            {
                Validate(property.Value, additional, rootSchema, $"{path}.{property.Name}", references);
            }
        }
    }

    private static void ValidateScalarConstraints(JsonElement instance, JsonElement schema, string path)
    {
        if (instance.ValueKind == JsonValueKind.String)
        {
            string value = instance.GetString()!;
            if (schema.TryGetProperty("minLength", out JsonElement minimumLength) &&
                value.Length < minimumLength.GetInt32())
            {
                throw new InvalidOperationException(
                    $"Manifest string at '{path}' is shorter than schema minLength {minimumLength.GetInt32()}.");
            }
            if (schema.TryGetProperty("maxLength", out JsonElement maximumLength) &&
                value.Length > maximumLength.GetInt32())
            {
                throw new InvalidOperationException(
                    $"Manifest string at '{path}' is longer than schema maxLength {maximumLength.GetInt32()}.");
            }
            if (schema.TryGetProperty("pattern", out JsonElement pattern) &&
                !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException(
                    $"Manifest string at '{path}' does not match schema pattern '{pattern.GetString()}'.");
            }
        }

        if (instance.ValueKind == JsonValueKind.Number &&
            decimal.TryParse(instance.GetRawText(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal valueNumber))
        {
            if (schema.TryGetProperty("minimum", out JsonElement minimum) && valueNumber < minimum.GetDecimal())
            {
                throw new InvalidOperationException(
                    $"Manifest number at '{path}' is below schema minimum {minimum.GetRawText()}.");
            }
            if (schema.TryGetProperty("maximum", out JsonElement maximum) && valueNumber > maximum.GetDecimal())
            {
                throw new InvalidOperationException(
                    $"Manifest number at '{path}' is above schema maximum {maximum.GetRawText()}.");
            }
        }
    }

    private static bool MatchesType(JsonElement instance, JsonElement typeElement)
    {
        if (typeElement.ValueKind == JsonValueKind.Array)
        {
            return typeElement.EnumerateArray().Any(type => MatchesType(instance, type));
        }

        string expected = typeElement.GetString()
            ?? throw new InvalidOperationException("Schema type entry is null.");
        return expected switch
        {
            "array" => instance.ValueKind == JsonValueKind.Array,
            "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => instance.ValueKind == JsonValueKind.Number && IsInteger(instance),
            "null" => instance.ValueKind == JsonValueKind.Null,
            "number" => instance.ValueKind == JsonValueKind.Number,
            "object" => instance.ValueKind == JsonValueKind.Object,
            "string" => instance.ValueKind == JsonValueKind.String,
            _ => throw new InvalidOperationException($"Unsupported schema type '{expected}'.")
        };
    }

    private static bool IsInteger(JsonElement instance)
    {
        return decimal.TryParse(
            instance.GetRawText(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out decimal value) && decimal.Truncate(value) == value;
    }

    private static JsonElement ResolveReference(JsonElement rootSchema, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Only local JSON schema references are supported, found '{reference}'.");
        }

        JsonElement resolved = rootSchema;
        foreach (string token in reference[2..].Split('/'))
        {
            string propertyName = token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            resolved = resolved.GetProperty(propertyName);
        }

        return resolved;
    }
}
