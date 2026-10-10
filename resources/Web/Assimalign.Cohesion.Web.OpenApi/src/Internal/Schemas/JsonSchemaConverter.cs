using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

using Assimalign.Cohesion.OpenApi;

namespace Assimalign.Cohesion.Web.OpenApi.Internal;

/// <summary>
/// Converts the JSON Schema (draft 2020-12) node that <c>JsonSchemaExporter</c> produces, after
/// <see cref="OpenApiSchemaGenerator"/> has replaced object types with component references, into the
/// canonical <see cref="OpenApiSchema"/> model for one OpenAPI line.
/// </summary>
/// <remarks>
/// <para>
/// The model normalizes most version differences itself (nullability, exclusive bounds, boolean schemas,
/// the 3.1 vocabulary); the converter handles the three the model cannot know about:
/// </para>
/// <list type="bullet">
/// <item>A nullable component reference, which the generator writes as
/// <c>anyOf: [{ $ref }, { type: null }]</c>, becomes <c>allOf: [{ $ref }]</c> with <c>nullable: true</c>
/// for 3.0, where a <c>null</c> type does not exist.</item>
/// <item><c>const</c>, which 3.0 lacks, becomes a one-value <c>enum</c> there.</item>
/// <item>A type list with more than one non-null entry keeps one entry for 3.0, which allows a single
/// type: the string form when present, because the writer emits it.</item>
/// </list>
/// <para>
/// Only the keywords the exporter emits are mapped; anything else is dropped rather than guessed at.
/// </para>
/// </remarks>
internal static class JsonSchemaConverter
{
    /// <summary>
    /// Converts a schema node for <paramref name="version"/>.
    /// </summary>
    /// <param name="node">The JSON Schema node: an object, or the boolean schema <c>true</c>/<c>false</c>.</param>
    /// <param name="version">The OpenAPI line the schema is written for.</param>
    /// <returns>The equivalent schema model.</returns>
    public static OpenApiSchema Convert(JsonNode? node, OpenApiSpecVersion version)
    {
        if (node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return new OpenApiSchema { BooleanValue = value.GetValueKind() == JsonValueKind.True };
        }

        if (node is not JsonObject json)
        {
            return new OpenApiSchema();
        }

        if (TryGetNullableReference(json, out string? reference))
        {
            return CreateNullableReference(reference, version);
        }

        OpenApiSchema schema = new();

        foreach (KeyValuePair<string, JsonNode?> keyword in json)
        {
            JsonNode? argument = keyword.Value;

            switch (keyword.Key)
            {
                case "$ref":
                    schema.Reference = new OpenApiReference { Ref = GetString(argument) ?? string.Empty };
                    break;
                case "type":
                    ReadTypes(argument, schema, version);
                    break;
                case "format":
                    schema.Format = GetString(argument);
                    break;
                case "pattern":
                    schema.Pattern = GetString(argument);
                    break;
                case "title":
                    schema.Title = GetString(argument);
                    break;
                case "description":
                    schema.Description = GetString(argument);
                    break;
                case "$comment":
                    schema.Comment = GetString(argument);
                    break;
                case "contentEncoding":
                    schema.ContentEncoding = GetString(argument);
                    break;
                case "contentMediaType":
                    schema.ContentMediaType = GetString(argument);
                    break;
                case "enum":
                    ReadEnum(argument, schema);
                    break;
                case "const":
                    if (version == OpenApiSpecVersion.V3_0)
                    {
                        schema.Enum.Add(ToNode(argument));
                    }
                    else
                    {
                        schema.Const = ToNode(argument);
                    }
                    break;
                case "default":
                    schema.Default = ToNode(argument);
                    break;
                case "properties":
                    if (argument is JsonObject properties)
                    {
                        foreach (KeyValuePair<string, JsonNode?> property in properties)
                        {
                            schema.Properties[property.Key] = Convert(property.Value, version);
                        }
                    }
                    break;
                case "required":
                    if (argument is JsonArray required)
                    {
                        foreach (JsonNode? name in required)
                        {
                            if (GetString(name) is { } text)
                            {
                                schema.Required.Add(text);
                            }
                        }
                    }
                    break;
                case "additionalProperties":
                    if (argument is JsonValue allowed && allowed.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                    {
                        schema.AdditionalPropertiesAllowed = allowed.GetValueKind() == JsonValueKind.True;
                    }
                    else
                    {
                        schema.AdditionalProperties = Convert(argument, version);
                    }
                    break;
                case "items":
                    schema.Items = Convert(argument, version);
                    break;
                case "anyOf":
                    ReadSchemas(argument, schema.AnyOf, version);
                    break;
                case "oneOf":
                    ReadSchemas(argument, schema.OneOf, version);
                    break;
                case "allOf":
                    ReadSchemas(argument, schema.AllOf, version);
                    break;
                case "not":
                    schema.Not = Convert(argument, version);
                    break;
                case "minLength":
                    schema.MinLength = GetInt32(argument);
                    break;
                case "maxLength":
                    schema.MaxLength = GetInt32(argument);
                    break;
                case "minItems":
                    schema.MinItems = GetInt32(argument);
                    break;
                case "maxItems":
                    schema.MaxItems = GetInt32(argument);
                    break;
                case "minProperties":
                    schema.MinProperties = GetInt32(argument);
                    break;
                case "maxProperties":
                    schema.MaxProperties = GetInt32(argument);
                    break;
                case "minimum":
                    schema.Minimum = GetDouble(argument);
                    break;
                case "maximum":
                    schema.Maximum = GetDouble(argument);
                    break;
                case "exclusiveMinimum":
                    schema.ExclusiveMinimum = GetDouble(argument);
                    break;
                case "exclusiveMaximum":
                    schema.ExclusiveMaximum = GetDouble(argument);
                    break;
                case "multipleOf":
                    schema.MultipleOf = GetDouble(argument);
                    break;
                case "uniqueItems":
                    schema.UniqueItems = GetBoolean(argument);
                    break;
                case "readOnly":
                    schema.ReadOnly = GetBoolean(argument) ?? false;
                    break;
                case "writeOnly":
                    schema.WriteOnly = GetBoolean(argument) ?? false;
                    break;
                case "deprecated":
                    schema.Deprecated = GetBoolean(argument) ?? false;
                    break;
            }
        }

        return schema;
    }

    /// <summary>
    /// Creates a reference to a component that admits <see langword="null"/>, in the form
    /// <paramref name="version"/> can express.
    /// </summary>
    /// <param name="reference">The component reference (<c>#/components/schemas/Name</c>).</param>
    /// <param name="version">The OpenAPI line the schema is written for.</param>
    /// <returns>The schema.</returns>
    public static OpenApiSchema CreateNullableReference(string reference, OpenApiSpecVersion version)
    {
        OpenApiSchema target = new() { Reference = new OpenApiReference { Ref = reference } };
        OpenApiSchema schema = new();

        if (version == OpenApiSpecVersion.V3_0)
        {
            // 3.0 has no null type; nullable beside allOf is the established spelling.
            schema.AllOf.Add(target);
            schema.Nullable = true;
        }
        else
        {
            schema.AnyOf.Add(target);
            schema.AnyOf.Add(new OpenApiSchema { Type = SchemaType.Null });
        }

        return schema;
    }

    /// <summary>
    /// Converts a JSON value (an <c>enum</c> entry, a <c>default</c>, a <c>const</c>) into the model's
    /// value tree.
    /// </summary>
    /// <param name="node">The JSON value.</param>
    /// <returns>The equivalent node.</returns>
    public static OpenApiNode ToNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return OpenApiValueNode.Null;
            case JsonObject json:
                OpenApiObjectNode result = new();
                foreach (KeyValuePair<string, JsonNode?> member in json)
                {
                    result[member.Key] = ToNode(member.Value);
                }
                return result;
            case JsonArray array:
                OpenApiArrayNode items = new();
                foreach (JsonNode? item in array)
                {
                    items.Add(ToNode(item));
                }
                return items;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        return OpenApiValueNode.String(value.GetValue<string>());
                    case JsonValueKind.True:
                        return OpenApiValueNode.Boolean(true);
                    case JsonValueKind.False:
                        return OpenApiValueNode.Boolean(false);
                    case JsonValueKind.Number when TryGetInteger(value, out long integer):
                        return OpenApiValueNode.Integer(integer);
                    case JsonValueKind.Number when TryGetReal(value, out double real):
                        return OpenApiValueNode.Double(real);
                    default:
                        return OpenApiValueNode.Null;
                }
            default:
                return OpenApiValueNode.Null;
        }
    }

    // anyOf: [{ "$ref": ... }, { "type": "null" }] and nothing else: the generator's spelling of a
    // nullable component reference.
    private static bool TryGetNullableReference(JsonObject json, out string reference)
    {
        reference = string.Empty;

        if (json.Count != 1
            || json["anyOf"] is not JsonArray { Count: 2 } candidates
            || candidates[0] is not JsonObject { Count: 1 } target
            || GetString(target["$ref"]) is not { } value
            || candidates[1] is not JsonObject { Count: 1 } nullType
            || GetString(nullType["type"]) != "null")
        {
            return false;
        }

        reference = value;
        return true;
    }

    private static void ReadTypes(JsonNode? argument, OpenApiSchema schema, OpenApiSpecVersion version)
    {
        List<SchemaType> types = [];
        bool nullable = false;

        if (argument is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                AddType(GetString(item));
            }
        }
        else
        {
            AddType(GetString(argument));
        }

        if (version == OpenApiSpecVersion.V3_0 && types.Count > 1)
        {
            // 3.0 allows one type. The exporter lists several only when numbers are written as strings, so
            // the string form is the one the wire carries.
            SchemaType single = types.Contains(SchemaType.String) ? SchemaType.String : types[0];
            types.Clear();
            types.Add(single);
        }

        foreach (SchemaType type in types)
        {
            schema.Types.Add(type);
        }

        if (nullable)
        {
            schema.Nullable = true;
        }

        void AddType(string? name)
        {
            SchemaType? type = name switch
            {
                "string" => SchemaType.String,
                "integer" => SchemaType.Integer,
                "number" => SchemaType.Number,
                "boolean" => SchemaType.Boolean,
                "object" => SchemaType.Object,
                "array" => SchemaType.Array,
                _ => null
            };

            if (name == "null")
            {
                nullable = true;
            }
            else if (type is { } known && !types.Contains(known))
            {
                types.Add(known);
            }
        }
    }

    private static void ReadEnum(JsonNode? argument, OpenApiSchema schema)
    {
        if (argument is not JsonArray values)
        {
            return;
        }

        foreach (JsonNode? value in values)
        {
            // A null entry is kept: a nullable enum lists null among its values on every line.
            if (value is null)
            {
                schema.Nullable = true;
            }

            schema.Enum.Add(ToNode(value));
        }
    }

    private static void ReadSchemas(JsonNode? argument, IList<OpenApiSchema> target, OpenApiSpecVersion version)
    {
        if (argument is not JsonArray schemas)
        {
            return;
        }

        foreach (JsonNode? schema in schemas)
        {
            target.Add(Convert(schema, version));
        }
    }

    private static string? GetString(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static bool? GetBoolean(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValueKind() == JsonValueKind.True
            : null;

    private static int? GetInt32(JsonNode? node)
        => node is JsonValue value && TryGetInteger(value, out long number) && number is >= int.MinValue and <= int.MaxValue
            ? (int)number
            : null;

    private static double? GetDouble(JsonNode? node)
        => node is JsonValue value && TryGetReal(value, out double number) ? number : null;

    // A JsonValue holds either a parsed JsonElement, which converts to any numeric type, or the CLR
    // primitive it was created from, which converts only to its own type; try the integral shapes in turn.
    private static bool TryGetInteger(JsonValue value, out long number)
    {
        if (value.TryGetValue(out long int64))
        {
            number = int64;
            return true;
        }

        if (value.TryGetValue(out int int32))
        {
            number = int32;
            return true;
        }

        number = 0;
        return false;
    }

    private static bool TryGetReal(JsonValue value, out double number)
    {
        if (value.TryGetValue(out double real))
        {
            number = real;
            return true;
        }

        if (TryGetInteger(value, out long integer))
        {
            number = integer;
            return true;
        }

        if (value.TryGetValue(out decimal fixedPoint))
        {
            number = (double)fixedPoint;
            return true;
        }

        if (value.TryGetValue(out float single))
        {
            number = single;
            return true;
        }

        number = 0;
        return false;
    }
}
