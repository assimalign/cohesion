using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.OpenApi;
using Assimalign.Cohesion.OpenApi.Attributes;
using Assimalign.Cohesion.Web.Serialization;

namespace Assimalign.Cohesion.Web.OpenApi.Internal;

/// <summary>
/// Produces the schemas of one OpenAPI document from the application's System.Text.Json contracts: the
/// <see cref="JsonTypeInfo"/> the registered JSON writer serializes a type with, exported by
/// <see cref="JsonSchemaExporter"/>, with every object type lifted into a named component.
/// </summary>
/// <remarks>
/// <para>
/// The exporter works from contract metadata the application's source-generated context already holds,
/// so no CLR member is ever reflected over and the path is NativeAOT-safe. It inlines everything and
/// writes repeated or recursive shapes as JSON pointers relative to its own root, which do not survive
/// being split into components. The exporter's transform hook therefore rewrites each node as it is
/// produced, children first:
/// </para>
/// <list type="bullet">
/// <item>An object type (a POCO or record, not a polymorphic branch) becomes a reference to
/// <c>#/components/schemas/{Name}</c>, and its body is exported once, separately, as that component. A
/// nullable usage becomes <c>anyOf</c> of the reference and <c>null</c>.</item>
/// <item>A pointer to a collection or dictionary the exporter already wrote elsewhere is replaced by an
/// inline export of that type, because the pointer is relative to an export that is about to be split.</item>
/// <item>A number the writer emits as a JSON number loses the string alternative and pattern that
/// <see cref="JsonNumberHandling.AllowReadingFromString"/> (on by default in the web defaults) adds,
/// and gains its OpenAPI format (<c>int32</c>, <c>int64</c>, <c>double</c>, …).</item>
/// </list>
/// <para>
/// One generator serves one document build: component names are allocated in the order types are first
/// met, which follows the route table and is therefore stable from build to build.
/// </para>
/// </remarks>
internal sealed class OpenApiSchemaGenerator
{
    private const string componentPrefix = "#/components/schemas/";

    private readonly IHttpContentSerializationFeature? _serialization;
    private readonly OpenApiSpecVersion _version;
    private readonly JsonSchemaExporterOptions _exporterOptions;
    private readonly Dictionary<Type, string> _componentNames = new();
    private readonly HashSet<string> _allocatedNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonNode> _jsonComponents = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, JsonNode> _usages = new();
    private readonly Queue<(string Name, JsonTypeInfo TypeInfo)> _pending = new();
    private readonly HashSet<Type> _inlineExports = new();
    private string? _problemDetailsName;
    private Type? _componentRoot;

    public OpenApiSchemaGenerator(IHttpContentSerializationFeature? serialization, OpenApiSpecVersion version)
    {
        _serialization = serialization;
        _version = version;
        _exporterOptions = new JsonSchemaExporterOptions
        {
            // A described value is not null unless its contract says so: a root, an item, or a property
            // the nullable annotations mark non-null.
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };
    }

    /// <summary>
    /// Resolves the contract the application's JSON writer serializes <paramref name="type"/> with.
    /// </summary>
    /// <param name="type">The described CLR type.</param>
    /// <param name="typeInfo">The contract, when one is registered.</param>
    /// <returns><see langword="true"/> when the application registered a contract for the type.</returns>
    public bool TryGetJsonTypeInfo(Type type, [NotNullWhen(true)] out JsonTypeInfo? typeInfo)
    {
        if (_serialization is not null)
        {
            return _serialization.TryGetJsonTypeInfo(type, out typeInfo);
        }

        typeInfo = null;
        return false;
    }

    /// <summary>
    /// Describes a value of the contract's type at a usage site (a request body, a response): a component
    /// reference for an object type, an inline schema otherwise.
    /// </summary>
    /// <param name="typeInfo">The contract.</param>
    /// <returns>The schema for the usage site.</returns>
    public OpenApiSchema GetSchema(JsonTypeInfo typeInfo)
    {
        // Many endpoints read or return the same type: export its usage once and convert it per use, so
        // each operation still gets a schema instance of its own.
        if (!_usages.TryGetValue(typeInfo.Type, out JsonNode? node))
        {
            node = Export(typeInfo, componentRoot: null);
            _usages[typeInfo.Type] = node;
        }

        while (_pending.TryDequeue(out (string Name, JsonTypeInfo TypeInfo) component))
        {
            Type componentType = Nullable.GetUnderlyingType(component.TypeInfo.Type) ?? component.TypeInfo.Type;
            JsonNode body = Export(component.TypeInfo, componentType);

            // A component registered from a Nullable<T> usage whose T has no contract of its own is exported
            // from the nullable contract; the component itself is the non-null shape.
            RemoveNull(body);
            _jsonComponents[component.Name] = body;
        }

        return JsonSchemaConverter.Convert(node, _version);
    }

    /// <summary>
    /// Gets a reference to the RFC 9457 problem-details component, registering it on first use. It
    /// describes <c>application/problem+json</c> bodies: the binding failures the Web.Api thunk answers and
    /// any <c>Assimalign.Cohesion.Web.ProblemDetails</c> response an endpoint declares.
    /// </summary>
    /// <returns>The reference.</returns>
    public OpenApiSchema GetProblemDetailsSchema()
    {
        _problemDetailsName ??= AllocateName(typeof(ProblemDetails));

        return new OpenApiSchema { Reference = new OpenApiReference { Ref = componentPrefix + _problemDetailsName } };
    }

    /// <summary>
    /// Gets the components registered so far, ordered by name.
    /// </summary>
    /// <returns>The component metadata, each carrying its complete schema.</returns>
    public IReadOnlyList<OpenApiSchemaMetadata> GetComponents()
    {
        SortedDictionary<string, OpenApiSchema> components = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, JsonNode> component in _jsonComponents)
        {
            components[component.Key] = JsonSchemaConverter.Convert(component.Value, _version);
        }

        if (_problemDetailsName is not null)
        {
            components[_problemDetailsName] = CreateProblemDetailsSchema();
        }

        List<OpenApiSchemaMetadata> metadata = new(components.Count);

        foreach (KeyValuePair<string, OpenApiSchema> component in components)
        {
            metadata.Add(new OpenApiSchemaMetadata { Name = component.Key, Schema = component.Value });
        }

        return metadata;
    }

    private JsonNode Export(JsonTypeInfo typeInfo, Type? componentRoot)
    {
        Type? previous = _componentRoot;
        _componentRoot = componentRoot;

        try
        {
            return typeInfo.GetJsonSchemaAsNode(_exporterOptions);
        }
        finally
        {
            _componentRoot = previous;
        }
    }

    // Invoked by the exporter for every schema node it produces, children before their parent.
    private JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        JsonTypeInfo typeInfo = context.TypeInfo;
        bool isPointer = IsPointer(node);

        // Polymorphic branches (BaseTypeInfo set) stay inline: each is a partial schema completed by the
        // base type's own keywords.
        if (typeInfo.Kind == JsonTypeInfoKind.Object && context.BaseTypeInfo is null)
        {
            Type componentType = Nullable.GetUnderlyingType(typeInfo.Type) ?? typeInfo.Type;

            if (context.Path.IsEmpty && componentType == _componentRoot)
            {
                return node; // the body of the component being exported
            }

            string name = RegisterComponent(componentType, typeInfo);
            bool nullable = IsNullable(node)
                || (isPointer && context.PropertyInfo is { } property && (property.IsGetNullable || property.IsSetNullable));

            return CreateReference(name, nullable);
        }

        if (isPointer)
        {
            return ExportInline(context, typeInfo);
        }

        if (typeInfo.Kind == JsonTypeInfoKind.None && node is JsonObject schema)
        {
            NormalizeValue(context, typeInfo, schema);
        }

        return node;
    }

    private string RegisterComponent(Type componentType, JsonTypeInfo typeInfo)
    {
        if (_componentNames.TryGetValue(componentType, out string? name))
        {
            return name;
        }

        name = AllocateName(componentType);

        // Export the component from the underlying type's own contract when the usage was Nullable<T>.
        JsonTypeInfo componentTypeInfo = componentType != typeInfo.Type && typeInfo.Options.TryGetTypeInfo(componentType, out JsonTypeInfo? underlying)
            ? underlying
            : typeInfo;

        _pending.Enqueue((name, componentTypeInfo));
        return name;
    }

    // A collection or dictionary the exporter wrote earlier in the same export, as a pointer relative to
    // that export's root. Export it again here instead; a collection that contains itself stops at an
    // unconstrained schema.
    private JsonNode ExportInline(JsonSchemaExporterContext context, JsonTypeInfo typeInfo)
    {
        if (!_inlineExports.Add(typeInfo.Type))
        {
            return new JsonObject();
        }

        try
        {
            JsonNode inline = typeInfo.GetJsonSchemaAsNode(_exporterOptions);

            if (context.PropertyInfo is { } property && (property.IsGetNullable || property.IsSetNullable))
            {
                AddNull(inline);
            }

            return inline;
        }
        finally
        {
            _inlineExports.Remove(typeInfo.Type);
        }
    }

    private static void NormalizeValue(JsonSchemaExporterContext context, JsonTypeInfo typeInfo, JsonObject schema)
    {
        Type type = Nullable.GetUnderlyingType(typeInfo.Type) ?? typeInfo.Type;

        if (ClrSchemas.TryGetNumericFormat(type, out string? format))
        {
            JsonNumberHandling handling = context.PropertyInfo?.NumberHandling ?? typeInfo.NumberHandling ?? typeInfo.Options.NumberHandling;

            if ((handling & JsonNumberHandling.WriteAsString) == 0 && schema["type"] is JsonArray types)
            {
                // The writer emits a JSON number. Reading one from a string is a leniency of the reader;
                // the description states the canonical form.
                for (int i = types.Count - 1; i >= 0; i--)
                {
                    if (GetString(types[i]) == "string")
                    {
                        types.RemoveAt(i);
                    }
                }

                schema.Remove("pattern");

                if (types.Count == 1)
                {
                    schema["type"] = GetString(types[0]);
                }
            }

            if (format is not null && !schema.ContainsKey("format") && HasNumericType(schema))
            {
                schema["format"] = format;
            }

            return;
        }

        if (type == typeof(byte[]) && !schema.ContainsKey("format"))
        {
            schema["format"] = "byte"; // base64, as the JSON writer encodes it
            return;
        }

        // A string enum is written as an enum list without a type; state the type for tools that need one.
        if (!schema.ContainsKey("type") && schema["enum"] is JsonArray values && values.Count > 0)
        {
            bool hasNull = false;

            foreach (JsonNode? value in values)
            {
                if (value is null)
                {
                    hasNull = true;
                }
                else if (value is not JsonValue text || text.GetValueKind() != JsonValueKind.String)
                {
                    return;
                }
            }

            schema["type"] = hasNull ? new JsonArray("string", "null") : "string";
        }
    }

    private string AllocateName(Type type)
    {
        if (_componentNames.TryGetValue(type, out string? name))
        {
            return name;
        }

        string baseName = Sanitize(GetTypeName(type));
        name = baseName;

        for (int suffix = 2; !_allocatedNames.Add(name); suffix++)
        {
            name = baseName + suffix.ToString(CultureInfo.InvariantCulture);
        }

        _componentNames[type] = name;
        return name;
    }

    // Order, PageOfOrder, DictionaryOfStringAndOrder, OrderArray: readable, and stable for a given type.
    private static string GetTypeName(Type type)
    {
        if (type.IsArray && type.GetElementType() is { } element)
        {
            return GetTypeName(element) + "Array";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return GetTypeName(underlying);
        }

        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name;
        int arity = name.IndexOf('`');
        StringBuilder builder = new(arity > 0 ? name[..arity] : name);
        Type[] arguments = type.GetGenericArguments();

        builder.Append("Of");

        for (int i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
            {
                builder.Append("And");
            }

            builder.Append(GetTypeName(arguments[i]));
        }

        return builder.ToString();
    }

    // Component names must match ^[a-zA-Z0-9.\-_]+$.
    private static string Sanitize(string name)
    {
        StringBuilder builder = new(name.Length);

        foreach (char character in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        }

        return builder.Length == 0 ? "Schema" : builder.ToString();
    }

    private static JsonObject CreateReference(string name, bool nullable)
    {
        JsonObject reference = new() { ["$ref"] = componentPrefix + name };

        return nullable
            ? new JsonObject { ["anyOf"] = new JsonArray(reference, new JsonObject { ["type"] = "null" }) }
            : reference;
    }

    private static bool IsPointer(JsonNode node)
        => node is JsonObject { Count: 1 } json
            && GetString(json["$ref"]) is { } reference
            && reference.StartsWith('#')
            && !reference.StartsWith(componentPrefix, StringComparison.Ordinal);

    private static bool IsNullable(JsonNode node)
    {
        if (node is not JsonObject json || json["type"] is not JsonArray types)
        {
            return false;
        }

        foreach (JsonNode? type in types)
        {
            if (GetString(type) == "null")
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasNumericType(JsonObject schema)
    {
        switch (schema["type"])
        {
            case JsonValue single:
                return GetString(single) is "integer" or "number";
            case JsonArray types:
                foreach (JsonNode? type in types)
                {
                    if (GetString(type) is "integer" or "number")
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static void AddNull(JsonNode node)
    {
        if (node is not JsonObject json)
        {
            return;
        }

        switch (json["type"])
        {
            case JsonValue single when GetString(single) is { } name && name != "null":
                json["type"] = new JsonArray(name, "null");
                break;
            case JsonArray types when !IsNullable(json):
                // Typed as JsonNode so the non-generic JsonArray.Add(JsonNode) binds: the generic
                // Add<T>(T) would wrap the value through runtime code (IL2026/IL3050).
                JsonNode? nullType = JsonValue.Create("null");
                types.Add(nullType);
                break;
        }
    }

    private static void RemoveNull(JsonNode node)
    {
        if (node is not JsonObject json || json["type"] is not JsonArray types)
        {
            return;
        }

        for (int i = types.Count - 1; i >= 0; i--)
        {
            if (GetString(types[i]) == "null")
            {
                types.RemoveAt(i);
            }
        }

        if (types.Count == 1)
        {
            json["type"] = GetString(types[0]);
        }
    }

    private static string? GetString(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    // RFC 9457 §3.1: the five standard members, all optional; extension members are allowed.
    private static OpenApiSchema CreateProblemDetailsSchema()
    {
        OpenApiSchema schema = new()
        {
            Type = SchemaType.Object,
            Description = "A problem detail (RFC 9457) describing why the request failed."
        };

        schema.Properties["type"] = new OpenApiSchema { Type = SchemaType.String, Format = "uri-reference", Description = "A URI reference identifying the problem type." };
        schema.Properties["title"] = new OpenApiSchema { Type = SchemaType.String, Description = "A short summary of the problem type." };
        schema.Properties["status"] = new OpenApiSchema { Type = SchemaType.Integer, Format = "int32", Description = "The HTTP status code of this occurrence." };
        schema.Properties["detail"] = new OpenApiSchema { Type = SchemaType.String, Description = "An explanation specific to this occurrence." };
        schema.Properties["instance"] = new OpenApiSchema { Type = SchemaType.String, Format = "uri-reference", Description = "A URI reference identifying this occurrence." };

        return schema;
    }
}
