using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// Compiles schema JSON into <see cref="SchemaNode"/> trees: assigns base URIs and anchors per the
/// dialect's identification rules, compiles subschemas where each keyword's layout places them, and
/// builds each keyword's evaluator.
/// </summary>
[DebuggerDisplay("Nodes = {Nodes.Count}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class SchemaCompiler
{
    private readonly JsonSchemaDocument _document;
    private readonly JsonSchemaRegistry _registry;
    private readonly TimeSpan _regexMatchTimeout;
    private readonly List<SchemaResource> _resources = new();
    private readonly List<SchemaNode> _nodes = new();

    public SchemaCompiler(JsonSchemaDocument document, JsonSchemaRegistry registry, TimeSpan regexMatchTimeout)
    {
        _document = document;
        _registry = registry;
        _regexMatchTimeout = regexMatchTimeout;
    }

    /// <summary>Gets the resources the compilation created, in document order.</summary>
    public IReadOnlyList<SchemaResource> Resources => _resources;

    /// <summary>Gets the nodes the compilation created.</summary>
    public IReadOnlyList<SchemaNode> Nodes => _nodes;

    /// <summary>
    /// Compiles a document's root: reads <c>$schema</c> for the dialect and the root identifier for the
    /// base URI, then the whole tree.
    /// </summary>
    public SchemaNode CompileRoot(JsonElement element, UriReference initialBase, bool synthetic, JsonSchemaDialect defaultDialect)
    {
        string location = _document.FormatLocation(string.Empty);
        JsonSchemaDialect dialect = defaultDialect;
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$schema", out JsonElement metaSchema))
        {
            if (metaSchema.ValueKind != JsonValueKind.String)
            {
                throw JsonSchemaProcessingException.KeywordValueInvalid("$schema", location);
            }

            dialect = _registry.ResolveDialect(metaSchema.GetString()!, location);
        }

        UriReference baseUri = initialBase;
        bool isSynthetic = synthetic;
        string? anchor = null;
        if (element.ValueKind == JsonValueKind.Object && TryGetIdentifier(element, dialect, location, out string? keyword, out UriReference? identifier))
        {
            if (IsFragmentOnly(identifier))
            {
                if (dialect.Version <= JsonSchemaVersion.Draft07 && !string.IsNullOrEmpty(identifier.Fragment))
                {
                    anchor = DecodeAnchor(identifier.Fragment, keyword, location);
                }
            }
            else
            {
                anchor = ReadIdentifierFragment(identifier, dialect, keyword, location);
                baseUri = initialBase.Resolve(identifier).WithFragment(null);
                isSynthetic = synthetic && !identifier.IsAbsolute;
            }
        }

        SchemaResource resource = new(_document, baseUri, isSynthetic, dialect, string.Empty);
        _resources.Add(resource);
        return Compile(element, string.Empty, string.Empty, resource, resource, anchor);
    }

    /// <summary>
    /// Compiles a location that no keyword placed a subschema at, on first reference to it.
    /// </summary>
    public SchemaNode CompileDetached(JsonElement element, string pointer, SchemaResource enclosing)
    {
        SchemaResource resource = ResolveResource(element, pointer, enclosing, out string? anchor);
        return Compile(element, pointer, string.Empty, enclosing, resource, anchor);
    }

    private SchemaNode CompileSubschema(JsonElement element, string pointer, string relativePath, SchemaResource parent)
    {
        SchemaResource resource = ResolveResource(element, pointer, parent, out string? anchor);
        return Compile(element, pointer, relativePath, parent, resource, anchor);
    }

    private SchemaNode Compile(JsonElement element, string pointer, string relativePath, SchemaResource parent, SchemaResource resource, string? anchor)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            SchemaNode boolean = new(resource, pointer, relativePath, element);
            if (!ReferenceEquals(resource, parent) || pointer.Length == 0)
            {
                resource.Root ??= boolean;
            }

            _nodes.Add(boolean);
            return boolean;
        }

        string location = _document.FormatLocation(pointer);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw JsonSchemaProcessingException.SchemaKindInvalid(location);
        }

        SchemaNode node = new(resource, pointer, relativePath, element);
        _nodes.Add(node);
        if (resource.Root is null && resource.DocumentPointer == pointer)
        {
            resource.Root = node;
        }

        if (anchor is not null)
        {
            AddAnchor(resource, anchor, node, dynamic: false, location);
        }

        JsonSchemaDialect dialect = resource.Dialect;
        bool referenceOverrides = dialect.Version <= JsonSchemaVersion.Draft07 && HasKeyword(element, dialect, KeywordKind.Ref);
        List<SchemaKeyword> keywords = new();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!dialect.TryGetKeyword(property.Name, out JsonSchemaKeywordDefinition? definition))
            {
                if (!referenceOverrides && dialect.Version >= JsonSchemaVersion.Draft201909)
                {
                    keywords.Add(new AnnotationKeyword(node, property.Name, property.Value));
                }

                continue;
            }

            // Before 2019-09, "$ref" replaces its schema object; only definitions are still walked so
            // the identifiers they declare stay addressable.
            if (referenceOverrides && definition.Kind is not (KeywordKind.Ref or KeywordKind.Defs))
            {
                continue;
            }

            SchemaKeyword? keyword = CreateKeyword(definition, property.Value, node, element, location);
            if (keyword is not null)
            {
                keywords.Add(keyword);
            }
        }

        node.Initialize(keywords);
        return node;
    }

    /// <summary>
    /// Returns the resource a subschema belongs to: a new one when it declares an identifier that
    /// changes the base URI, or its parent's.
    /// </summary>
    private SchemaResource ResolveResource(JsonElement element, string pointer, SchemaResource parent, out string? anchor)
    {
        anchor = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return parent;
        }

        JsonSchemaDialect dialect = parent.Dialect;
        string location = _document.FormatLocation(pointer);
        if (!TryGetIdentifier(element, dialect, location, out string? keyword, out UriReference? identifier))
        {
            return parent;
        }

        // Before 2019-09 a "$ref" makes every sibling, "$id" included, ignored.
        if (dialect.Version <= JsonSchemaVersion.Draft07 && HasKeyword(element, dialect, KeywordKind.Ref))
        {
            return parent;
        }

        if (IsFragmentOnly(identifier))
        {
            if (!string.IsNullOrEmpty(identifier.Fragment))
            {
                if (dialect.Version >= JsonSchemaVersion.Draft201909)
                {
                    throw JsonSchemaProcessingException.IdFragmentInvalid(keyword, location);
                }

                // A fragment-only id is a location-independent identifier: an anchor (draft 7 §8.2.3).
                anchor = DecodeAnchor(identifier.Fragment, keyword, location);
            }

            return parent;
        }

        anchor = ReadIdentifierFragment(identifier, dialect, keyword, location);
        UriReference resolved = parent.Base.Resolve(identifier).WithFragment(null);
        if (string.Equals(resolved.ToNormalizedString(includeFragment: false), parent.BaseText, StringComparison.Ordinal))
        {
            return parent;
        }

        // 2019-09 and later let an embedded resource change dialect with its own "$schema".
        JsonSchemaDialect resourceDialect = dialect;
        if (dialect.Version >= JsonSchemaVersion.Draft201909 && element.TryGetProperty("$schema", out JsonElement metaSchema))
        {
            if (metaSchema.ValueKind != JsonValueKind.String)
            {
                throw JsonSchemaProcessingException.KeywordValueInvalid("$schema", location);
            }

            resourceDialect = _registry.ResolveDialect(metaSchema.GetString()!, location);
        }

        SchemaResource resource = new(_document, resolved, parent.IsSynthetic && !identifier.IsAbsolute, resourceDialect, pointer);
        _resources.Add(resource);
        return resource;
    }

    private static bool TryGetIdentifier(JsonElement element, JsonSchemaDialect dialect, string location, out string keyword, out UriReference identifier)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (dialect.TryGetKeyword(property.Name, out JsonSchemaKeywordDefinition? definition) && definition.Kind == KeywordKind.Id)
            {
                keyword = property.Name;
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw JsonSchemaProcessingException.KeywordValueInvalid(keyword, location);
                }

                if (!UriReference.TryParse(property.Value.GetString()!, out UriReference? parsed))
                {
                    throw JsonSchemaProcessingException.UriReferenceInvalid(keyword, location);
                }

                identifier = parsed;
                return true;
            }
        }

        keyword = string.Empty;
        identifier = null!;
        return false;
    }

    private static string? ReadIdentifierFragment(UriReference identifier, JsonSchemaDialect dialect, string keyword, string location)
    {
        if (string.IsNullOrEmpty(identifier.Fragment))
        {
            return null;
        }

        if (dialect.Version >= JsonSchemaVersion.Draft201909)
        {
            throw JsonSchemaProcessingException.IdFragmentInvalid(keyword, location);
        }

        // Before 2019-09, "http://example.com/a#foo" names resource a and anchor foo within it.
        return DecodeAnchor(identifier.Fragment, keyword, location);
    }

    private static string DecodeAnchor(string fragment, string keyword, string location)
    {
        string? decoded = UriReference.PercentDecode(fragment);
        if (decoded is null)
        {
            throw JsonSchemaProcessingException.UriReferenceInvalid(keyword, location);
        }

        return decoded;
    }

    private static bool IsFragmentOnly(UriReference reference) =>
        reference.Scheme is null && reference.Authority is null && reference.Path.Length == 0 && reference.Query is null;

    private static bool HasKeyword(JsonElement element, JsonSchemaDialect dialect, KeywordKind kind)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (dialect.TryGetKeyword(property.Name, out JsonSchemaKeywordDefinition? definition) && definition.Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetSibling(JsonElement element, JsonSchemaDialect dialect, KeywordKind kind, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (dialect.TryGetKeyword(property.Name, out JsonSchemaKeywordDefinition? definition) && definition.Kind == kind)
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void AddAnchor(SchemaResource resource, string name, SchemaNode node, bool dynamic, string location)
    {
        if (resource.Anchors.TryGetValue(name, out SchemaNode? existing) && !ReferenceEquals(existing, node))
        {
            throw JsonSchemaProcessingException.ResourceDuplicate(resource.BaseText + "#" + name);
        }

        resource.Anchors[name] = node;
        if (dynamic)
        {
            resource.DynamicAnchors[name] = node;
        }
    }

    private SchemaKeyword? CreateKeyword(JsonSchemaKeywordDefinition definition, JsonElement value, SchemaNode node, JsonElement schema, string location)
    {
        string name = definition.Name;
        SchemaResource resource = node.Resource;
        JsonSchemaDialect dialect = resource.Dialect;
        switch (definition.Kind)
        {
            case KeywordKind.Schema:
            case KeywordKind.Id:
            case KeywordKind.Vocabulary:
            case KeywordKind.Comment:
                return null;

            case KeywordKind.Anchor:
            case KeywordKind.DynamicAnchor:
                if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length == 0)
                {
                    throw JsonSchemaProcessingException.AnchorInvalid(name, location);
                }

                AddAnchor(resource, value.GetString()!, node, definition.Kind == KeywordKind.DynamicAnchor, location);
                return null;

            case KeywordKind.RecursiveAnchor:
                RequireKind(value, JsonValueKind.True, JsonValueKind.False, name, location);
                if (value.ValueKind == JsonValueKind.True && node.IsResourceRoot)
                {
                    resource.RecursiveAnchor = true;
                }

                return null;

            case KeywordKind.Defs:
                CompileMap(value, node, name, location);
                return null;

            case KeywordKind.Ref:
                return new RefKeyword(node, name, value, ParseReference(value, resource, name, location));

            case KeywordKind.DynamicRef:
                return new DynamicRefKeyword(node, name, value, ParseReference(value, resource, name, location));

            case KeywordKind.RecursiveRef:
                return new RecursiveRefKeyword(node, name, value, ParseReference(value, resource, name, location));

            case KeywordKind.AllOf:
                return new AllOfKeyword(node, name, value, CompileArray(value, node, name, location));

            case KeywordKind.AnyOf:
                return new AnyOfKeyword(node, name, value, CompileArray(value, node, name, location));

            case KeywordKind.OneOf:
                return new OneOfKeyword(node, name, value, CompileArray(value, node, name, location));

            case KeywordKind.Not:
                return new NotKeyword(node, name, value, CompileSchema(value, node, name));

            case KeywordKind.If:
                return new IfKeyword(node, name, value, CompileSchema(value, node, name));

            case KeywordKind.Then:
                return new ConditionalBranchKeyword(node, name, value, CompileSchema(value, node, name), isThen: true);

            case KeywordKind.Else:
                return new ConditionalBranchKeyword(node, name, value, CompileSchema(value, node, name), isThen: false);

            case KeywordKind.Properties:
                return new PropertiesKeyword(node, name, value, CompileMap(value, node, name, location));

            case KeywordKind.PatternProperties:
            {
                Dictionary<string, SchemaNode> map = CompileMap(value, node, name, location);
                List<PatternPropertiesKeyword.Entry> entries = new(map.Count);
                foreach (KeyValuePair<string, SchemaNode> entry in map)
                {
                    entries.Add(new PatternPropertiesKeyword.Entry(EcmaPattern.Create(entry.Key, _regexMatchTimeout, location), entry.Value));
                }

                return new PatternPropertiesKeyword(node, name, value, entries.ToArray());
            }

            case KeywordKind.AdditionalProperties:
                return new AdditionalPropertiesKeyword(node, name, value, CompileSchema(value, node, name));

            case KeywordKind.PropertyNames:
                return new PropertyNamesKeyword(node, name, value, CompileSchema(value, node, name));

            case KeywordKind.DependentSchemas:
                return new DependentSchemasKeyword(node, name, value, CompileMap(value, node, name, location));

            case KeywordKind.Dependencies:
                return CreateDependencies(value, node, name, location);

            case KeywordKind.PrefixItems:
                return new PrefixItemsKeyword(node, name, value, CompileArray(value, node, name, location));

            case KeywordKind.Items:
                if (value.ValueKind == JsonValueKind.Array)
                {
                    throw JsonSchemaProcessingException.KeywordValueInvalid(name, location);
                }

                return new ItemsKeyword(node, name, value, CompileSchema(value, node, name));

            case KeywordKind.ItemsLegacy:
                return value.ValueKind == JsonValueKind.Array
                    ? new LegacyItemsKeyword(node, name, value, null, CompileArray(value, node, name, location))
                    : new LegacyItemsKeyword(node, name, value, CompileSchema(value, node, name), null);

            case KeywordKind.AdditionalItems:
                return new AdditionalItemsKeyword(node, name, value, CompileSchema(value, node, name));

            case KeywordKind.Contains:
            case KeywordKind.ContainsLegacy:
            {
                SchemaNode subschema = CompileSchema(value, node, name);
                long minimum = 1;
                long? maximum = null;
                if (TryGetSibling(schema, dialect, KeywordKind.MinContains, out JsonElement minContains))
                {
                    minimum = ReadCount(minContains, "minContains", location);
                }

                if (TryGetSibling(schema, dialect, KeywordKind.MaxContains, out JsonElement maxContains))
                {
                    maximum = ReadCount(maxContains, "maxContains", location);
                }

                return new ContainsKeyword(node, name, value, subschema, minimum, maximum, annotates: definition.Kind == KeywordKind.Contains);
            }

            case KeywordKind.UnevaluatedProperties:
                return new UnevaluatedPropertiesKeyword(node, name, value, CompileSchema(value, node, name));

            case KeywordKind.UnevaluatedItems:
                return new UnevaluatedItemsKeyword(node, name, value, CompileSchema(value, node, name));

            case KeywordKind.Type:
                return TypeKeyword.Create(node, name, value, dialect.Version == JsonSchemaVersion.Draft04, location);

            case KeywordKind.Enum:
                RequireKind(value, JsonValueKind.Array, JsonValueKind.Array, name, location);
                return new EnumKeyword(node, name, value);

            case KeywordKind.Const:
                return new ConstKeyword(node, name, value);

            case KeywordKind.MultipleOf:
            {
                JsonNumber divisor = ReadNumber(value, name, location);
                if (divisor.IsZero || divisor.Negative)
                {
                    throw JsonSchemaProcessingException.KeywordValueInvalid(name, location);
                }

                return new MultipleOfKeyword(node, name, value, divisor);
            }

            case KeywordKind.Maximum:
                return new NumberBoundKeyword(node, name, value, ReadNumber(value, name, location), upper: true, exclusive: false);

            case KeywordKind.ExclusiveMaximum:
                return new NumberBoundKeyword(node, name, value, ReadNumber(value, name, location), upper: true, exclusive: true);

            case KeywordKind.Minimum:
                return new NumberBoundKeyword(node, name, value, ReadNumber(value, name, location), upper: false, exclusive: false);

            case KeywordKind.ExclusiveMinimum:
                return new NumberBoundKeyword(node, name, value, ReadNumber(value, name, location), upper: false, exclusive: true);

            case KeywordKind.MaximumDraft04:
                return new NumberBoundKeyword(node, name, value, ReadNumber(value, name, location), upper: true, exclusive: ReadDraft04Exclusive(schema, dialect, KeywordKind.ExclusiveMaximumDraft04, location));

            case KeywordKind.MinimumDraft04:
                return new NumberBoundKeyword(node, name, value, ReadNumber(value, name, location), upper: false, exclusive: ReadDraft04Exclusive(schema, dialect, KeywordKind.ExclusiveMinimumDraft04, location));

            case KeywordKind.ExclusiveMaximumDraft04:
            case KeywordKind.ExclusiveMinimumDraft04:
                RequireKind(value, JsonValueKind.True, JsonValueKind.False, name, location);
                return null;

            case KeywordKind.MaxLength:
                return new LengthKeyword(node, name, value, ReadCount(value, name, location), upper: true);

            case KeywordKind.MinLength:
                return new LengthKeyword(node, name, value, ReadCount(value, name, location), upper: false);

            case KeywordKind.Pattern:
                RequireKind(value, JsonValueKind.String, JsonValueKind.String, name, location);
                return new PatternKeyword(node, name, value, EcmaPattern.Create(value.GetString()!, _regexMatchTimeout, location));

            case KeywordKind.MaxItems:
                return new ItemCountKeyword(node, name, value, ReadCount(value, name, location), upper: true);

            case KeywordKind.MinItems:
                return new ItemCountKeyword(node, name, value, ReadCount(value, name, location), upper: false);

            case KeywordKind.UniqueItems:
                RequireKind(value, JsonValueKind.True, JsonValueKind.False, name, location);
                return value.ValueKind == JsonValueKind.True ? new UniqueItemsKeyword(node, name, value) : null;

            case KeywordKind.MaxContains:
            case KeywordKind.MinContains:
                // Read by a sibling "contains"; without one they do nothing.
                ReadCount(value, name, location);
                return null;

            case KeywordKind.MaxProperties:
                return new PropertyCountKeyword(node, name, value, ReadCount(value, name, location), upper: true);

            case KeywordKind.MinProperties:
                return new PropertyCountKeyword(node, name, value, ReadCount(value, name, location), upper: false);

            case KeywordKind.Required:
                return new RequiredKeyword(node, name, value, ReadStringArray(value, name, location));

            case KeywordKind.DependentRequired:
            {
                RequireKind(value, JsonValueKind.Object, JsonValueKind.Object, name, location);
                List<KeyValuePair<string, string[]>> dependencies = new();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    dependencies.Add(new KeyValuePair<string, string[]>(property.Name, ReadStringArray(property.Value, name, location)));
                }

                return new DependentRequiredKeyword(node, name, value, dependencies.ToArray());
            }

            case KeywordKind.Annotation:
                return new AnnotationKeyword(node, name, value);

            case KeywordKind.Format:
            case KeywordKind.FormatAnnotation:
                RequireKind(value, JsonValueKind.String, JsonValueKind.String, name, location);
                return new FormatKeyword(node, name, value, alwaysAssert: false);

            case KeywordKind.FormatAssertion:
                RequireKind(value, JsonValueKind.String, JsonValueKind.String, name, location);
                return new FormatKeyword(node, name, value, alwaysAssert: true);

            case KeywordKind.ContentEncoding:
                RequireKind(value, JsonValueKind.String, JsonValueKind.String, name, location);
                return new ContentEncodingKeyword(node, name, value);

            case KeywordKind.ContentMediaType:
                RequireKind(value, JsonValueKind.String, JsonValueKind.String, name, location);
                return new ContentMediaTypeKeyword(node, name, value);

            case KeywordKind.ContentSchema:
                return new ContentSchemaKeyword(node, name, value, CompileSchema(value, node, name));

            case KeywordKind.Custom:
                return CreateCustom(definition, value, node, location);

            default:
                throw new UnreachableException();
        }
    }

    private SchemaKeyword CreateCustom(JsonSchemaKeywordDefinition definition, JsonElement value, SchemaNode node, string location)
    {
        Dictionary<string, SchemaNode> subschemas = new(StringComparer.Ordinal);
        string name = definition.Name;
        switch (definition.SubschemaLayout)
        {
            case JsonSchemaSubschemaLayout.Schema:
                subschemas[string.Empty] = CompileSchema(value, node, name);
                break;
            case JsonSchemaSubschemaLayout.SchemaArray:
                AddArray(subschemas, CompileArray(value, node, name, location));
                break;
            case JsonSchemaSubschemaLayout.SchemaMap:
                foreach (KeyValuePair<string, SchemaNode> entry in CompileMap(value, node, name, location))
                {
                    subschemas["/" + JsonPointer.EscapeToken(entry.Key)] = entry.Value;
                }

                break;
            case JsonSchemaSubschemaLayout.SchemaOrSchemaArray:
                if (value.ValueKind == JsonValueKind.Array)
                {
                    AddArray(subschemas, CompileArray(value, node, name, location));
                }
                else
                {
                    subschemas[string.Empty] = CompileSchema(value, node, name);
                }

                break;
        }

        IJsonSchemaKeyword evaluator = definition.Factory!(value)
            ?? throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.KeywordFactoryResultNull, name));
        return new CustomKeyword(node, name, value, evaluator, subschemas);

        static void AddArray(Dictionary<string, SchemaNode> subschemas, SchemaNode[] nodes)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                subschemas["/" + i.ToString(CultureInfo.InvariantCulture)] = nodes[i];
            }
        }
    }

    private DependenciesKeyword CreateDependencies(JsonElement value, SchemaNode node, string name, string location)
    {
        RequireKind(value, JsonValueKind.Object, JsonValueKind.Object, name, location);
        List<DependenciesKeyword.Dependency> dependencies = new();
        string keywordPointer = node.Pointer + "/" + JsonPointer.EscapeToken(name);
        string keywordPath = "/" + JsonPointer.EscapeToken(name);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                dependencies.Add(new DependenciesKeyword.Dependency(property.Name, ReadStringArray(property.Value, name, location), null));
            }
            else
            {
                string segment = "/" + JsonPointer.EscapeToken(property.Name);
                SchemaNode subschema = CompileSubschema(property.Value, keywordPointer + segment, keywordPath + segment, node.Resource);
                dependencies.Add(new DependenciesKeyword.Dependency(property.Name, null, subschema));
            }
        }

        return new DependenciesKeyword(node, name, value, dependencies.ToArray());
    }

    private SchemaNode CompileSchema(JsonElement value, SchemaNode node, string keyword)
    {
        string segment = "/" + JsonPointer.EscapeToken(keyword);
        return CompileSubschema(value, node.Pointer + segment, segment, node.Resource);
    }

    private SchemaNode[] CompileArray(JsonElement value, SchemaNode node, string keyword, string location)
    {
        RequireKind(value, JsonValueKind.Array, JsonValueKind.Array, keyword, location);
        string segment = "/" + JsonPointer.EscapeToken(keyword);
        SchemaNode[] nodes = new SchemaNode[value.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            string suffix = segment + "/" + index.ToString(CultureInfo.InvariantCulture);
            nodes[index] = CompileSubschema(item, node.Pointer + suffix, suffix, node.Resource);
            index++;
        }

        return nodes;
    }

    private Dictionary<string, SchemaNode> CompileMap(JsonElement value, SchemaNode node, string keyword, string location)
    {
        RequireKind(value, JsonValueKind.Object, JsonValueKind.Object, keyword, location);
        string segment = "/" + JsonPointer.EscapeToken(keyword);
        Dictionary<string, SchemaNode> map = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            string suffix = segment + "/" + JsonPointer.EscapeToken(property.Name);
            map[property.Name] = CompileSubschema(property.Value, node.Pointer + suffix, suffix, node.Resource);
        }

        return map;
    }

    private static UriReference ParseReference(JsonElement value, SchemaResource resource, string keyword, string location)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw JsonSchemaProcessingException.KeywordValueInvalid(keyword, location);
        }

        if (!UriReference.TryParse(value.GetString()!, out UriReference? reference))
        {
            throw JsonSchemaProcessingException.UriReferenceInvalid(keyword, location);
        }

        return resource.Base.Resolve(reference);
    }

    private static bool ReadDraft04Exclusive(JsonElement schema, JsonSchemaDialect dialect, KeywordKind kind, string location)
    {
        if (!TryGetSibling(schema, dialect, kind, out JsonElement value))
        {
            return false;
        }

        RequireKind(value, JsonValueKind.True, JsonValueKind.False, kind == KeywordKind.ExclusiveMaximumDraft04 ? "exclusiveMaximum" : "exclusiveMinimum", location);
        return value.ValueKind == JsonValueKind.True;
    }

    private static JsonNumber ReadNumber(JsonElement value, string keyword, string location)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw JsonSchemaProcessingException.KeywordValueInvalid(keyword, location);
        }

        return JsonNumber.FromElement(value);
    }

    /// <summary>
    /// Reads a non-negative integer limit. 2019-09 and later allow it to be written with a fraction of
    /// zero (<c>2.0</c>); values beyond <see cref="long.MaxValue"/> saturate.
    /// </summary>
    private static long ReadCount(JsonElement value, string keyword, string location)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw JsonSchemaProcessingException.KeywordValueInvalid(keyword, location);
        }

        if (value.TryGetInt64(out long count))
        {
            if (count < 0)
            {
                throw JsonSchemaProcessingException.KeywordValueInvalid(keyword, location);
            }

            return count;
        }

        JsonNumber number = JsonNumber.FromElement(value);
        if (!number.IsInteger || number.Negative)
        {
            throw JsonSchemaProcessingException.KeywordValueInvalid(keyword, location);
        }

        return number.CompareTo(JsonNumber.FromInt64(long.MaxValue)) >= 0 ? long.MaxValue : (long)(number.Significand * System.Numerics.BigInteger.Pow(10, (int)number.Exponent));
    }

    private static string[] ReadStringArray(JsonElement value, string keyword, string location)
    {
        RequireKind(value, JsonValueKind.Array, JsonValueKind.Array, keyword, location);
        string[] items = new string[value.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw JsonSchemaProcessingException.KeywordValueInvalid(keyword, location);
            }

            items[index++] = item.GetString()!;
        }

        return items;
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind, JsonValueKind alternative, string keyword, string location)
    {
        if (value.ValueKind != kind && value.ValueKind != alternative)
        {
            throw JsonSchemaProcessingException.KeywordValueInvalid(keyword, location);
        }
    }

    private sealed class DebugView
    {
        private readonly SchemaCompiler _compiler;

        public DebugView(SchemaCompiler compiler)
        {
            _compiler = compiler;
        }

        public IReadOnlyList<SchemaResource> Resources => _compiler.Resources;

        public IReadOnlyList<SchemaNode> Nodes => _compiler.Nodes;
    }
}
