using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// Generates one schema document from a System.Text.Json contract.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JsonSchemaExporter"/> describes a contract inline. Where it meets a type and property it
/// has described before, it writes a JSON Pointer to the earlier description, relative to its own root:
/// that ends recursion, but a type used in several places is still described in full each time, and a
/// pointer into the middle of the tree breaks as soon as anything around it changes. Its transform hook
/// sees every node with the contract the node describes, so the export is recorded with that context and
/// then rebuilt:
/// </para>
/// <list type="number">
/// <item><b>Analysis</b> walks the export the way the document will be written, following pointers. It
/// counts the places each object type is used, walking each object type's description once, and finds the
/// collections that contain themselves with no object type in between.</item>
/// <item><b>Emission</b> writes the document. An object type used in more than one place, which includes
/// a type used in its own description, becomes a definition and each use a reference to it; a type used
/// once is described inline. Each use applies its own nullability and default.</item>
/// <item><b>Assembly</b> stamps <c>$schema</c> and <c>$id</c>, adds the definitions, and in draft-07 moves
/// each <c>$ref</c> that has siblings into an <c>allOf</c>.</item>
/// </list>
/// <para>
/// One generator serves one call. Definitions are named in the order analysis first meets them, which
/// follows the contract's property order, so a contract always generates the same document.
/// </para>
/// </remarks>
[DebuggerDisplay("{TypeInfo.Type.Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SchemaGenerator
{
    private const string referenceKeyword = "$ref";
    private const string typeKeyword = "type";
    private const string anyOfKeyword = "anyOf";
    private const string defaultKeyword = "default";
    private const string nullType = "null";

    // The exporter writes 2020-12, so its output is walked with that dialect's keywords.
    private static readonly JsonSchemaDialect _exportDialect = JsonSchemaDialect.Draft202012;

    private readonly Dictionary<JsonNode, ExportedNode> _exported = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JsonNode, JsonNode?> _targets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Type, Definition> _objects = new();
    private readonly Dictionary<JsonNode, Definition> _collections = new(ReferenceEqualityComparer.Instance);
    private readonly List<JsonNode> _cyclicCollections = new();
    private readonly List<Definition> _definitions = new();
    private readonly string _definitionsKeyword;
    private List<JsonNode> _collectionPath = new();
    private JsonNode _export = null!;
    private Definition? _root;

    private SchemaGenerator(JsonTypeInfo typeInfo, JsonSchemaGenerationOptions options)
    {
        TypeInfo = typeInfo;
        Options = options;
        _definitionsKeyword = options.Dialect.Version == JsonSchemaVersion.Draft07 ? "definitions" : "$defs";
    }

    /// <summary>Gets the contract of the root type.</summary>
    public JsonTypeInfo TypeInfo { get; }

    /// <summary>Gets the generation options.</summary>
    public JsonSchemaGenerationOptions Options { get; }

    private enum NodeKind
    {
        /// <summary>A node the exporter wrote without a contract, such as a discriminator's <c>const</c>.</summary>
        Structural,

        /// <summary>A value with no members or items of its own: a primitive, an enum, or a converter's value.</summary>
        Leaf,

        /// <summary>A property with its own converter, which the exporter described as the type's default contract.</summary>
        Opaque,

        /// <summary>A derived type, as one alternative of its polymorphic base type.</summary>
        Branch,

        /// <summary>A type with members, polymorphic bases included.</summary>
        Object,

        /// <summary>A collection or dictionary.</summary>
        Collection,
    }

    /// <summary>
    /// Generates the document for a contract and loads it.
    /// </summary>
    /// <param name="typeInfo">The contract of the root type.</param>
    /// <param name="options">The generation options.</param>
    /// <param name="documentOptions">The options the document is loaded with.</param>
    /// <returns>The loaded document.</returns>
    public static JsonSchemaDocument Generate(JsonTypeInfo typeInfo, JsonSchemaGenerationOptions options, JsonSchemaDocumentOptions? documentOptions)
    {
        JsonObject document = new SchemaGenerator(typeInfo, options).Run();

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            document.WriteTo(writer);
        }

        return JsonSchemaDocument.Parse(buffer.WrittenSpan, documentOptions);
    }

    private JsonObject Run()
    {
        _export = TypeInfo.GetJsonSchemaAsNode(new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = Options.TreatNullObliviousAsNonNullable,
            TransformSchemaNode = Record,
        });

        Analyze();
        NameDefinitions();

        JsonNode root = EmitRoot();
        List<KeyValuePair<string, JsonNode>> definitions = EmitDefinitions();
        JsonObject document = Assemble(root, definitions);

        if (Options.Dialect.Version == JsonSchemaVersion.Draft07)
        {
            WrapReferences(document, Options.Dialect);
        }

        return document;
    }

    // Invoked by the exporter for every node it describes a contract with, children first.
    private JsonNode Record(JsonSchemaExporterContext context, JsonNode node)
    {
        _exported[node] = new ExportedNode(context.TypeInfo, context.PropertyInfo, context.BaseTypeInfo);
        return node;
    }

    // ---------------------------------------------------------------------------------------------
    // Analysis
    // ---------------------------------------------------------------------------------------------

    private void Analyze()
    {
        // A collection found to contain itself is walked as a definition in the next pass, which may
        // change the counts the first pass made; the walk repeats until it finds no new one.
        int cyclicCount;
        do
        {
            cyclicCount = _cyclicCollections.Count;
            _objects.Clear();
            _collections.Clear();
            _definitions.Clear();
            _collectionPath.Clear();

            foreach (JsonNode collection in _cyclicCollections)
            {
                _collections.Add(collection, new Definition(collection, _exported[collection].TypeInfo, isCollection: true));
            }

            Visit(_export);
        }
        while (_cyclicCollections.Count != cyclicCount);

        ExportedNode? rootInfo = GetInfo(_export, _export);
        _root = Classify(_export, rootInfo) switch
        {
            NodeKind.Object => _objects[GetDefinedType(rootInfo!)],
            NodeKind.Collection when _collections.TryGetValue(_export, out Definition? collection) => collection,
            _ => null,
        };
    }

    private void Visit(JsonNode? node)
    {
        if (node is null || Resolve(node) is not { } target)
        {
            return;
        }

        ExportedNode? info = GetInfo(node, target);
        switch (Classify(target, info))
        {
            case NodeKind.Object:
                Enter(GetObjectDefinition(info!, target));
                break;
            case NodeKind.Collection:
                if (_collections.TryGetValue(target, out Definition? collection))
                {
                    Enter(collection);
                }
                else if (_collectionPath.Contains(target))
                {
                    if (!_cyclicCollections.Contains(target))
                    {
                        _cyclicCollections.Add(target);
                    }
                }
                else
                {
                    _collectionPath.Add(target);
                    VisitChildren(target);
                    _collectionPath.RemoveAt(_collectionPath.Count - 1);
                }

                break;
            case NodeKind.Branch:
            case NodeKind.Structural:
                VisitChildren(target);
                break;
        }
    }

    // A definition's description is walked once. A cycle through it is broken by referring to it, so the
    // collections around it start a path of their own.
    private void Enter(Definition definition)
    {
        if (definition.References++ > 0)
        {
            return;
        }

        _definitions.Add(definition);
        List<JsonNode> path = _collectionPath;
        _collectionPath = new List<JsonNode>();
        VisitChildren(definition.Body);
        _collectionPath = path;
    }

    private void VisitChildren(JsonNode node)
    {
        if (node is not JsonObject schema)
        {
            return;
        }

        foreach (KeyValuePair<string, JsonNode?> keyword in schema)
        {
            switch (GetLayout(_exportDialect, keyword.Key))
            {
                case JsonSchemaSubschemaLayout.Schema:
                    Visit(keyword.Value);
                    break;
                case JsonSchemaSubschemaLayout.SchemaArray:
                case JsonSchemaSubschemaLayout.SchemaOrSchemaArray when keyword.Value is JsonArray:
                    if (keyword.Value is JsonArray items)
                    {
                        foreach (JsonNode? item in items)
                        {
                            Visit(item);
                        }
                    }

                    break;
                case JsonSchemaSubschemaLayout.SchemaOrSchemaArray:
                    Visit(keyword.Value);
                    break;
                case JsonSchemaSubschemaLayout.SchemaMap:
                    if (keyword.Value is JsonObject members)
                    {
                        foreach (KeyValuePair<string, JsonNode?> member in members)
                        {
                            Visit(member.Value);
                        }
                    }

                    break;
            }
        }
    }

    private Definition GetObjectDefinition(ExportedNode info, JsonNode body)
    {
        Type type = GetDefinedType(info);
        if (!_objects.TryGetValue(type, out Definition? definition))
        {
            // A Nullable<T> use describes T; its definition carries T's own contract.
            JsonTypeInfo typeInfo = info.TypeInfo;
            if (typeInfo.Type != type && typeInfo.Options.TryGetTypeInfo(type, out JsonTypeInfo? underlying))
            {
                typeInfo = underlying;
            }

            definition = new Definition(body, typeInfo, isCollection: false);
            _objects.Add(type, definition);
        }

        return definition;
    }

    private void NameDefinitions()
    {
        ExportedNode? rootInfo = GetInfo(_export, _export);
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (Definition definition in _definitions)
        {
            if (definition == _root)
            {
                // The root type's definition stays the root, which its references name "#", unless they
                // would then also accept the null the root itself allows.
                definition.Hoisted = !definition.IsCollection && definition.References > 1 && IsNullable(rootInfo!);
            }
            else
            {
                definition.Hoisted = definition.IsCollection || definition.References > 1;
            }

            if (!definition.Hoisted)
            {
                continue;
            }

            string baseName = Sanitize(GetTypeName(definition.TypeInfo.Type));
            string name = baseName;
            for (int suffix = 2; !names.Add(name); suffix++)
            {
                name = baseName + suffix.ToString(CultureInfo.InvariantCulture);
            }

            definition.Name = name;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Emission
    // ---------------------------------------------------------------------------------------------

    private JsonNode EmitRoot()
    {
        if (_root is null || _root.Hoisted)
        {
            return EmitUse(_export)!;
        }

        ExportedNode rootInfo = GetInfo(_export, _export)!;
        JsonNode body = EmitDefinitionBody(_root);
        if (!_root.IsCollection)
        {
            body = ApplyUse(body, rootInfo, hasDefault: false, defaultValue: null, isReference: false);
        }

        return Transform(body, rootInfo, isReference: false);
    }

    private List<KeyValuePair<string, JsonNode>> EmitDefinitions()
    {
        List<Definition> hoisted = new();
        foreach (Definition definition in _definitions)
        {
            if (definition.Hoisted)
            {
                hoisted.Add(definition);
            }
        }

        hoisted.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));

        List<KeyValuePair<string, JsonNode>> definitions = new(hoisted.Count);
        foreach (Definition definition in hoisted)
        {
            ExportedNode info = new(definition.TypeInfo, propertyInfo: null, baseTypeInfo: null);
            definitions.Add(new KeyValuePair<string, JsonNode>(definition.Name!, Transform(EmitDefinitionBody(definition), info, isReference: false)));
        }

        return definitions;
    }

    // Writes the schema for one place in the export: a property's value, an item, an alternative.
    private JsonNode? EmitUse(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (Resolve(node) is not { } target)
        {
            return node.DeepClone();
        }

        ExportedNode? info = GetInfo(node, target);
        switch (Classify(target, info))
        {
            case NodeKind.Structural:
                return EmitStructure(target, stripNull: false);
            case NodeKind.Branch:
                // A derived value is never null; the use of the base type decides whether null is allowed.
                return Transform(EmitStructure(target, stripNull: true), info, isReference: false);
            case NodeKind.Opaque:
                return Transform(JsonValue.Create(true), info, isReference: false);
            case NodeKind.Object:
            {
                Definition definition = _objects[GetDefinedType(info!)];
                bool hasDefault = TryGetDefault(node, out JsonNode? defaultValue);
                if (definition.Hoisted || definition == _root)
                {
                    JsonNode reference = ApplyUse(CreateReference(definition), info, hasDefault, defaultValue, isReference: true);
                    return Transform(reference, info, isReference: true);
                }

                JsonNode inline = ApplyUse(EmitDefinitionBody(definition), info, hasDefault, defaultValue, isReference: false);
                return Transform(inline, info, isReference: false);
            }
            case NodeKind.Collection:
                // Every pointer to a collection names the type and property the collection was written
                // for, so it shares that collection's nullability and default.
                if (_collections.TryGetValue(target, out Definition? collection))
                {
                    return Transform(CreateReference(collection), info, isReference: true);
                }

                return Transform(EmitStructure(target, stripNull: false), info, isReference: false);
            default:
                return Transform(target.DeepClone(), info, isReference: false);
        }
    }

    private JsonNode EmitDefinitionBody(Definition definition)
    {
        JsonNode body = EmitStructure(definition.Body, stripNull: false);
        if (!definition.IsCollection && body is JsonObject schema)
        {
            // The export wrote the body for the use it met first; each use applies its own.
            schema.Remove(defaultKeyword);
            StripNull(schema);
        }

        return body;
    }

    private JsonNode EmitStructure(JsonNode node, bool stripNull)
    {
        if (node is not JsonObject schema)
        {
            return node.DeepClone();
        }

        JsonObject result = new();
        foreach (KeyValuePair<string, JsonNode?> keyword in schema)
        {
            result[keyword.Key] = EmitKeyword(keyword.Key, keyword.Value);
        }

        if (stripNull)
        {
            StripNull(result);
        }

        return result;
    }

    private JsonNode? EmitKeyword(string keyword, JsonNode? value)
    {
        switch (GetLayout(_exportDialect, keyword))
        {
            case JsonSchemaSubschemaLayout.Schema:
                return EmitUse(value);
            case JsonSchemaSubschemaLayout.SchemaArray:
            case JsonSchemaSubschemaLayout.SchemaOrSchemaArray when value is JsonArray:
                if (value is JsonArray items)
                {
                    JsonArray result = new();
                    foreach (JsonNode? item in items)
                    {
                        result.Add(EmitUse(item));
                    }

                    return result;
                }

                break;
            case JsonSchemaSubschemaLayout.SchemaOrSchemaArray:
                return EmitUse(value);
            case JsonSchemaSubschemaLayout.SchemaMap:
                if (value is JsonObject members)
                {
                    JsonObject result = new();
                    foreach (KeyValuePair<string, JsonNode?> member in members)
                    {
                        result[member.Key] = EmitUse(member.Value);
                    }

                    return result;
                }

                break;
        }

        return value?.DeepClone();
    }

    private JsonObject CreateReference(Definition definition) => new()
    {
        [referenceKeyword] = definition.Hoisted ? "#/" + _definitionsKeyword + "/" + definition.Name : "#",
    };

    // Applies what belongs to one use of a type rather than to the type: whether null is allowed there,
    // and the default of the constructor parameter the property binds to.
    private JsonNode ApplyUse(JsonNode node, ExportedNode? info, bool hasDefault, JsonNode? defaultValue, bool isReference)
    {
        if (info is not null && IsNullable(info))
        {
            node = isReference ? new JsonObject { [anyOfKeyword] = new JsonArray(node, CreateNullSchema()) } : AddNull(node);
        }

        if (hasDefault)
        {
            JsonObject schema = AsObject(node);
            schema[defaultKeyword] = defaultValue?.DeepClone();
            node = schema;
        }

        return node;
    }

    // The exporter's own rule, applied to the use: a property's nullable annotations, Nullable<T>, or a
    // null-oblivious reference type.
    private bool IsNullable(ExportedNode info)
    {
        if (info.PropertyInfo is { } property)
        {
            return property.IsGetNullable || property.IsSetNullable;
        }

        Type type = info.TypeInfo.Type;
        return type.IsValueType ? Nullable.GetUnderlyingType(type) is not null : !Options.TreatNullObliviousAsNonNullable;
    }

    private JsonNode Transform(JsonNode node, ExportedNode? info, bool isReference)
    {
        if (Options.TransformSchemaNode is not { } transform || info is null)
        {
            return node;
        }

        JsonNode? result = transform(new GenerationContext(info.TypeInfo, info.PropertyInfo, info.BaseTypeInfo, isReference), node);
        if (result is null)
        {
            throw new InvalidOperationException(ErrorMessages.TransformResultNull);
        }

        if (result.Parent is not null)
        {
            throw new InvalidOperationException(ErrorMessages.TransformResultHasParent);
        }

        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Assembly
    // ---------------------------------------------------------------------------------------------

    private JsonObject Assemble(JsonNode root, List<KeyValuePair<string, JsonNode>> definitions)
    {
        JsonObject document = new() { ["$schema"] = Options.Dialect.Id.OriginalString };
        if (Options.Id is { } id)
        {
            document["$id"] = id.OriginalString;
        }

        JsonObject schema = AsObject(root);
        List<KeyValuePair<string, JsonNode?>> keywords = new(schema);
        JsonObject? existing = null;

        foreach (KeyValuePair<string, JsonNode?> keyword in keywords)
        {
            schema.Remove(keyword.Key);
            if (keyword.Key == "$schema" || (keyword.Key == "$id" && Options.Id is not null))
            {
                continue;
            }

            if (keyword.Key == _definitionsKeyword && keyword.Value is JsonObject transformed)
            {
                existing = transformed;
                continue;
            }

            document[keyword.Key] = keyword.Value;
        }

        if (definitions.Count > 0 || existing is not null)
        {
            JsonObject container = existing ?? new JsonObject();
            foreach (KeyValuePair<string, JsonNode> definition in definitions)
            {
                if (container.ContainsKey(definition.Key))
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DefinitionNameConflict, definition.Key));
                }

                container[definition.Key] = definition.Value;
            }

            document[_definitionsKeyword] = container;
        }

        return document;
    }

    // Draft-07 ignores every sibling of "$ref" (Core §8.3), so a reference that has siblings is applied
    // from an "allOf" instead.
    private static void WrapReferences(JsonNode? node, JsonSchemaDialect dialect)
    {
        if (node is not JsonObject schema)
        {
            return;
        }

        foreach (KeyValuePair<string, JsonNode?> keyword in schema)
        {
            switch (GetLayout(dialect, keyword.Key))
            {
                case JsonSchemaSubschemaLayout.Schema:
                    WrapReferences(keyword.Value, dialect);
                    break;
                case JsonSchemaSubschemaLayout.SchemaArray:
                case JsonSchemaSubschemaLayout.SchemaOrSchemaArray:
                    if (keyword.Value is JsonArray items)
                    {
                        foreach (JsonNode? item in items)
                        {
                            WrapReferences(item, dialect);
                        }
                    }
                    else
                    {
                        WrapReferences(keyword.Value, dialect);
                    }

                    break;
                case JsonSchemaSubschemaLayout.SchemaMap:
                    if (keyword.Value is JsonObject members)
                    {
                        foreach (KeyValuePair<string, JsonNode?> member in members)
                        {
                            WrapReferences(member.Value, dialect);
                        }
                    }

                    break;
            }
        }

        if (schema.Count > 1 && schema.TryGetPropertyValue(referenceKeyword, out JsonNode? reference))
        {
            schema.Remove(referenceKeyword);
            JsonObject applied = new() { [referenceKeyword] = reference };
            if (schema["allOf"] is JsonArray allOf)
            {
                allOf.Insert(0, applied);
            }
            else
            {
                schema["allOf"] = new JsonArray(applied);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Export structure
    // ---------------------------------------------------------------------------------------------

    private ExportedNode? GetInfo(JsonNode node, JsonNode target) =>
        _exported.TryGetValue(node, out ExportedNode? info) ? info : _exported.GetValueOrDefault(target);

    private static NodeKind Classify(JsonNode target, ExportedNode? info)
    {
        if (info is null)
        {
            return target is JsonObject ? NodeKind.Structural : NodeKind.Leaf;
        }

        if (info.BaseTypeInfo is not null)
        {
            return NodeKind.Branch;
        }

        if (info.TypeInfo.Kind == JsonTypeInfoKind.None || target is not JsonObject)
        {
            return NodeKind.Leaf;
        }

        // The exporter describes such a property by its type's default contract, but the converter, not
        // the contract, decides what is written.
        if (info.PropertyInfo?.CustomConverter is not null)
        {
            return NodeKind.Opaque;
        }

        return info.TypeInfo.Kind == JsonTypeInfoKind.Object ? NodeKind.Object : NodeKind.Collection;
    }

    private static Type GetDefinedType(ExportedNode info) =>
        Nullable.GetUnderlyingType(info.TypeInfo.Type) ?? info.TypeInfo.Type;

    // Returns the node an exporter pointer refers to, the node itself when it is not a pointer, or null
    // when the pointer leads nowhere.
    private JsonNode? Resolve(JsonNode node)
    {
        if (node is not JsonObject schema
            || !schema.TryGetPropertyValue(referenceKeyword, out JsonNode? reference)
            || reference is not JsonValue value
            || !value.TryGetValue(out string? pointer))
        {
            return node;
        }

        if (!_targets.TryGetValue(node, out JsonNode? target))
        {
            target = Walk(pointer);
            _targets.Add(node, target);
        }

        return target;
    }

    // The exporter writes "#" followed by a JSON Pointer it escapes for "~" and "/" only.
    private JsonNode? Walk(string pointer)
    {
        if (!pointer.StartsWith('#'))
        {
            return null;
        }

        JsonNode? current = _export;
        ReadOnlySpan<char> rest = pointer.AsSpan(1);
        while (!rest.IsEmpty)
        {
            if (rest[0] != '/')
            {
                return null;
            }

            rest = rest[1..];
            int end = rest.IndexOf('/');
            string token = (end < 0 ? rest : rest[..end]).ToString()
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            rest = end < 0 ? ReadOnlySpan<char>.Empty : rest[end..];

            current = current switch
            {
                JsonObject members => members.TryGetPropertyValue(token, out JsonNode? member) ? member : null,
                JsonArray items => int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < items.Count ? items[index] : null,
                _ => null,
            };

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static JsonSchemaSubschemaLayout GetLayout(JsonSchemaDialect dialect, string keyword)
    {
        // The members of "dependencies" are schemas or property lists; a list is no schema and is copied.
        if (keyword == "dependencies")
        {
            return JsonSchemaSubschemaLayout.SchemaMap;
        }

        return dialect.TryGetKeyword(keyword, out JsonSchemaKeywordDefinition? definition)
            ? definition.SubschemaLayout
            : JsonSchemaSubschemaLayout.None;
    }

    // ---------------------------------------------------------------------------------------------
    // Schema helpers
    // ---------------------------------------------------------------------------------------------

    private static bool TryGetDefault(JsonNode node, out JsonNode? value)
    {
        if (node is JsonObject schema && schema.TryGetPropertyValue(defaultKeyword, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    // Typed as JsonNode so JsonArray.Add binds its JsonNode overload, not Add<T>, which needs runtime code.
    private static JsonNode CreateNullSchema() => new JsonObject { [typeKeyword] = nullType };

    private static JsonObject AsObject(JsonNode node)
    {
        if (node is JsonObject schema)
        {
            return schema;
        }

        // A boolean schema: true accepts everything, false nothing.
        return node.GetValueKind() == JsonValueKind.False
            ? new JsonObject { ["not"] = new JsonObject() }
            : new JsonObject();
    }

    private static JsonNode AddNull(JsonNode node)
    {
        if (node is not JsonObject schema)
        {
            return node.GetValueKind() == JsonValueKind.False ? CreateNullSchema() : node;
        }

        switch (schema[typeKeyword])
        {
            case JsonValue single when GetString(single) is { } name:
                if (name != nullType)
                {
                    schema[typeKeyword] = new JsonArray((JsonNode)name, (JsonNode)nullType);
                }

                return schema;
            case JsonArray types:
                if (!ContainsString(types, nullType))
                {
                    JsonNode added = nullType;
                    types.Add(added);
                }

                return schema;
        }

        // No type to extend: a polymorphic base whose derived types differ in kind. Its alternatives gain
        // null as one more, and anything else is wrapped.
        if (schema.Count == 1 && schema[anyOfKeyword] is JsonArray alternatives)
        {
            alternatives.Add(CreateNullSchema());
            return schema;
        }

        return new JsonObject { [anyOfKeyword] = new JsonArray(schema, CreateNullSchema()) };
    }

    private static void StripNull(JsonObject schema)
    {
        if (schema[typeKeyword] is not JsonArray types || !ContainsString(types, nullType))
        {
            return;
        }

        List<string> remaining = new(types.Count);
        foreach (JsonNode? type in types)
        {
            if (GetString(type) is { } name && name != nullType)
            {
                remaining.Add(name);
            }
        }

        if (remaining.Count == 0 || remaining.Count != types.Count - 1)
        {
            return; // Only "null", or something other than type names: leave it as the exporter wrote it.
        }

        if (remaining.Count == 1)
        {
            schema[typeKeyword] = remaining[0];
            return;
        }

        JsonArray stripped = new();
        foreach (string name in remaining)
        {
            JsonNode added = name;
            stripped.Add(added);
        }

        schema[typeKeyword] = stripped;
    }

    private static bool ContainsString(JsonArray array, string value)
    {
        foreach (JsonNode? item in array)
        {
            if (GetString(item) == value)
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    // ---------------------------------------------------------------------------------------------
    // Names
    // ---------------------------------------------------------------------------------------------

    // Order, PageOfOrder, DictionaryOfStringAndOrder, OrderArray: readable, and stable for a type.
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

        string name = type.Name;
        if (!type.IsGenericType)
        {
            return name;
        }

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

    // Letters, digits, '.', '-' and '_' need no escaping in a JSON Pointer or a URI fragment, and are the
    // characters OpenAPI allows in a component name.
    private static string Sanitize(string name)
    {
        StringBuilder builder = new(name.Length);
        foreach (char character in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_');
        }

        return builder.Length == 0 ? "Schema" : builder.ToString();
    }

    /// <summary>A node of the export, with the contract the exporter described it for.</summary>
    private sealed class ExportedNode
    {
        public ExportedNode(JsonTypeInfo typeInfo, JsonPropertyInfo? propertyInfo, JsonTypeInfo? baseTypeInfo)
        {
            TypeInfo = typeInfo;
            PropertyInfo = propertyInfo;
            BaseTypeInfo = baseTypeInfo;
        }

        public JsonTypeInfo TypeInfo { get; }

        public JsonPropertyInfo? PropertyInfo { get; }

        public JsonTypeInfo? BaseTypeInfo { get; }
    }

    /// <summary>
    /// A schema the document may define once: an object type, or a collection that contains itself.
    /// </summary>
    private sealed class Definition
    {
        public Definition(JsonNode body, JsonTypeInfo typeInfo, bool isCollection)
        {
            Body = body;
            TypeInfo = typeInfo;
            IsCollection = isCollection;
        }

        /// <summary>Gets the node of the export that describes it in full.</summary>
        public JsonNode Body { get; }

        public JsonTypeInfo TypeInfo { get; }

        public bool IsCollection { get; }

        /// <summary>Gets or sets the number of places that use it, the root's own place included.</summary>
        public int References { get; set; }

        /// <summary>Gets or sets a value indicating whether it is written under the definitions keyword.</summary>
        public bool Hoisted { get; set; }

        public string? Name { get; set; }
    }

    private sealed class DebugView
    {
        private readonly SchemaGenerator _generator;

        public DebugView(SchemaGenerator generator)
        {
            _generator = generator;
        }

        public JsonTypeInfo TypeInfo => _generator.TypeInfo;

        public JsonSchemaGenerationOptions Options => _generator.Options;
    }
}
