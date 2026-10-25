using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Deviates from the repo namespace rule per the shared-source contract: this stateless
// wire implementation keeps its owning namespace in each protocol participant.
namespace Assimalign.Cohesion.Hosting.Resources.Internal;

internal static class ResourceStateWire
{
    public static string ComputeDigest(JsonElement value, string? excludedProperty = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, value, excludedProperty);
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    public static string ComputeValueDigest(bool exists, JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("exists", exists);
            writer.WritePropertyName("value");
            WriteCanonical(writer, value, null);
            writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    public static JsonElement WithContractDigest(JsonElement definition)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in definition.EnumerateObject())
            {
                if (property.Name != "contractDigest") { property.WriteTo(writer); }
            }
            if (!definition.TryGetProperty("supportsAbsence", out _)) { writer.WriteBoolean("supportsAbsence", false); }
            writer.WriteEndObject();
        }
        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        var signed = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(signed))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject()) { property.WriteTo(writer); }
            writer.WriteString("contractDigest", ComputeDigest(document.RootElement));
            writer.WriteEndObject();
        }
        using JsonDocument result = JsonDocument.Parse(signed.WrittenMemory);
        return result.RootElement.Clone();
    }

    public static bool IsValidDefinition(JsonElement definition, bool requireDigest)
    {
        if (definition.ValueKind != JsonValueKind.Object ||
            !definition.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String || !IsValidName(name.GetString()) ||
            !definition.TryGetProperty("payloadSchemaId", out JsonElement id) || id.ValueKind != JsonValueKind.String || !Uri.TryCreate(id.GetString(), UriKind.Absolute, out _) ||
            !definition.TryGetProperty("scope", out JsonElement scope) || scope.ValueKind != JsonValueKind.String || scope.GetString() is not ("everyInstance" or "sharedState") ||
            !definition.TryGetProperty("keySchema", out JsonElement keySchema) || !IsSupportedSchema(keySchema) ||
            !definition.TryGetProperty("payloadSchema", out JsonElement payloadSchema) || !IsSupportedSchema(payloadSchema) ||
            !definition.TryGetProperty("uses", out JsonElement uses) || uses.ValueKind != JsonValueKind.Array || uses.GetArrayLength() == 0) { return false; }
        var stages = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement use in uses.EnumerateArray())
        {
            if (use.ValueKind != JsonValueKind.String || use.GetString() is not ("orchestration" or "management") || !stages.Add(use.GetString()!)) { return false; }
        }
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in definition.EnumerateObject())
        {
            if (!properties.Add(property.Name) || property.Name is not ("name" or "payloadSchemaId" or "contractDigest" or "keySchema" or "payloadSchema" or "scope" or "uses" or "supportsAbsence")) { return false; }
        }
        if (definition.TryGetProperty("supportsAbsence", out JsonElement absence) && absence.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { return false; }
        return !requireDigest || (definition.TryGetProperty("contractDigest", out JsonElement digest) && digest.ValueKind == JsonValueKind.String &&
            definition.TryGetProperty("supportsAbsence", out _) && digest.GetString() == ComputeDigest(definition, "contractDigest"));
    }

    public static bool IsValidName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value is "." or "..") { return false; }
        return value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    public static bool IsValidManagementPath(string? value) =>
        !string.IsNullOrEmpty(value) && value[0] == '/' && value.IndexOfAny(['?', '#', '\\']) < 0 &&
        !value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character));

    public static bool ValidateValue(JsonElement schema, JsonElement value, int depth = 0)
    {
        if (depth > 64 || !IsSupportedSchema(schema, depth) || value.ValueKind == JsonValueKind.Undefined) { return false; }
        if (schema.TryGetProperty("type", out JsonElement type))
        {
            bool matches = type.ValueKind == JsonValueKind.String
                ? MatchesType(type.GetString(), value)
                : type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && MatchesType(item.GetString(), value));
            if (!matches) { return false; }
        }
        if (schema.TryGetProperty("const", out JsonElement constant) && ComputeDigest(constant) != ComputeDigest(value)) { return false; }
        if (schema.TryGetProperty("enum", out JsonElement choices) &&
            (choices.ValueKind != JsonValueKind.Array || !choices.EnumerateArray().Any(choice => ComputeDigest(choice) == ComputeDigest(value)))) { return false; }
        if (value.ValueKind == JsonValueKind.String)
        {
            int length = value.GetString()!.EnumerateRunes().Count();
            if ((schema.TryGetProperty("minLength", out JsonElement minimum) && length < minimum.GetInt32()) ||
                (schema.TryGetProperty("maxLength", out JsonElement maximum) && length > maximum.GetInt32())) { return false; }
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDecimal(out decimal number)) { return false; }
            if ((schema.TryGetProperty("minimum", out JsonElement minimum) && number < minimum.GetDecimal()) ||
                (schema.TryGetProperty("maximum", out JsonElement maximum) && number > maximum.GetDecimal()) ||
                (schema.TryGetProperty("exclusiveMinimum", out JsonElement lower) && number <= lower.GetDecimal()) ||
                (schema.TryGetProperty("exclusiveMaximum", out JsonElement upper) && number >= upper.GetDecimal())) { return false; }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out JsonElement required) &&
                (required.ValueKind != JsonValueKind.Array || required.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || !value.TryGetProperty(item.GetString()!, out _)))) { return false; }
            bool hasProperties = schema.TryGetProperty("properties", out JsonElement properties);
            if (hasProperties && properties.ValueKind != JsonValueKind.Object) { return false; }
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (hasProperties && properties.TryGetProperty(property.Name, out JsonElement child))
                {
                    if (!ValidateValue(child, property.Value, depth + 1)) { return false; }
                }
                else if (schema.TryGetProperty("additionalProperties", out JsonElement additional))
                {
                    if (additional.ValueKind == JsonValueKind.False ||
                        (additional.ValueKind == JsonValueKind.Object && !ValidateValue(additional, property.Value, depth + 1))) { return false; }
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            int length = value.GetArrayLength();
            if ((schema.TryGetProperty("minItems", out JsonElement minimum) && length < minimum.GetInt32()) ||
                (schema.TryGetProperty("maxItems", out JsonElement maximum) && length > maximum.GetInt32()) ||
                (schema.TryGetProperty("items", out JsonElement items) && value.EnumerateArray().Any(item => !ValidateValue(items, item, depth + 1)))) { return false; }
        }
        return true;
    }

    // The v1 protocol uses this deliberately bounded JSON Schema dialect. Refuse
    // unsupported validation keywords instead of accepting values without checking them.
    public static bool IsSupportedSchema(JsonElement schema, int depth = 0)
    {
        if (depth > 64 || schema.ValueKind != JsonValueKind.Object) { return false; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in schema.EnumerateObject())
        {
            if (!names.Add(property.Name)) { return false; }
            JsonElement value = property.Value;
            bool valid = property.Name switch
            {
                "$schema" or "$id" or "title" or "description" => value.ValueKind == JsonValueKind.String,
                "default" or "examples" or "const" => true,
                "enum" => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0,
                "type" => value.ValueKind == JsonValueKind.String ? IsType(value.GetString()) :
                    value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && IsType(item.GetString())),
                "required" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String),
                "properties" => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(item => IsSupportedSchema(item.Value, depth + 1)),
                "additionalProperties" => value.ValueKind is JsonValueKind.True or JsonValueKind.False || IsSupportedSchema(value, depth + 1),
                "items" => IsSupportedSchema(value, depth + 1),
                "minLength" or "maxLength" or "minItems" or "maxItems" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int length) && length >= 0,
                "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
                _ => false
            };
            if (!valid) { return false; }
        }
        return true;
    }

    private static bool IsType(string? value) => value is "object" or "array" or "string" or "boolean" or "number" or "integer" or "null";

    private static bool MatchesType(string? type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number) && decimal.Truncate(number) == number,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false
    };

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, string? excludedProperty)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    if (property.Name == excludedProperty) { continue; }
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, null);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray()) { WriteCanonical(writer, item, null); }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
