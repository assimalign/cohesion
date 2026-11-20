using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// A loaded JSON Schema document: its root schema, every subschema, and the schema resources it
/// registered.
/// </summary>
/// <remarks>
/// <para>
/// Loading compiles the schema once: it reads <c>$schema</c> to choose the dialect, assigns base URIs
/// from <c>$id</c> (or <c>id</c> in draft 4), registers every embedded resource and anchor in the
/// registry, and builds an evaluator for each keyword. References are resolved the first time they
/// are followed, so a document may refer to documents that load later.
/// </para>
/// <para>
/// A document is immutable once loaded and safe to evaluate from any number of threads.
/// </para>
/// </remarks>
[DebuggerDisplay("{Dialect.Id,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonSchemaDocument : IJsonSchema
{
    private static readonly JsonDocumentOptions _parseOptions = new() { MaxDepth = 1024 };

    private readonly ConcurrentDictionary<string, SchemaNode> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SchemaResource> _localResources = new(StringComparer.Ordinal);
    private readonly object _compileGate = new();
    private readonly TimeSpan _regexMatchTimeout;
    private SchemaNode _root = null!;

    private JsonSchemaDocument(JsonElement source, JsonSchemaRegistry registry, TimeSpan regexMatchTimeout)
    {
        Source = source;
        Registry = registry;
        _regexMatchTimeout = regexMatchTimeout;
    }

    /// <summary>
    /// Gets the base URI of the root schema resource, or <see langword="null"/> when the document has
    /// no identifier.
    /// </summary>
    public Uri? BaseUri => _root.BaseUri;

    /// <summary>
    /// Gets the dialect of the root schema.
    /// </summary>
    public JsonSchemaDialect Dialect => _root.Dialect;

    /// <summary>
    /// Gets the document's JSON value.
    /// </summary>
    public JsonElement Source { get; }

    /// <summary>
    /// Gets the registry the document is registered in.
    /// </summary>
    public JsonSchemaRegistry Registry { get; }

    /// <summary>
    /// Gets the location of the root schema: <see cref="JsonPointer.Root"/>.
    /// </summary>
    public JsonPointer Location => JsonPointer.Root;

    JsonSchemaDocument IJsonSchema.Document => this;

    internal SchemaNode Root => _root;

    /// <summary>
    /// Loads a schema document from JSON text.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="options">The load options, or <see langword="null"/> for <see cref="JsonSchemaDocumentOptions.Default"/>.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonSchemaException">
    /// The text is not JSON, the schema is malformed, its <c>$schema</c> names an unknown dialect, or a
    /// resource it declares is already registered.
    /// </exception>
    public static JsonSchemaDocument Parse(string json, JsonSchemaDocumentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonElement element;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, _parseOptions);
            element = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw JsonSchemaProcessingException.SchemaJsonInvalid(exception);
        }

        return Create(element, options);
    }

    /// <summary>
    /// Loads a schema document from UTF-8 JSON.
    /// </summary>
    /// <param name="utf8Json">The UTF-8 encoded JSON.</param>
    /// <param name="options">The load options, or <see langword="null"/> for <see cref="JsonSchemaDocumentOptions.Default"/>.</param>
    /// <returns>The document.</returns>
    /// <exception cref="JsonSchemaException">
    /// The text is not JSON, the schema is malformed, its <c>$schema</c> names an unknown dialect, or a
    /// resource it declares is already registered.
    /// </exception>
    public static JsonSchemaDocument Parse(ReadOnlySpan<byte> utf8Json, JsonSchemaDocumentOptions? options = null)
    {
        JsonElement element;
        try
        {
            Utf8JsonReader reader = new(utf8Json, new JsonReaderOptions { MaxDepth = _parseOptions.MaxDepth });
            element = JsonElement.ParseValue(ref reader);

            // The reader throws on anything but whitespace after the root value.
            reader.Read();
        }
        catch (JsonException exception)
        {
            throw JsonSchemaProcessingException.SchemaJsonInvalid(exception);
        }

        return Create(element, options);
    }

    /// <summary>
    /// Loads a schema document from a stream of UTF-8 JSON.
    /// </summary>
    /// <param name="utf8Json">The stream, read to its end and not closed.</param>
    /// <param name="options">The load options, or <see langword="null"/> for <see cref="JsonSchemaDocumentOptions.Default"/>.</param>
    /// <param name="cancellationToken">A token that cancels reading the stream.</param>
    /// <returns>The document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="utf8Json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonSchemaException">
    /// The content is not JSON, the schema is malformed, its <c>$schema</c> names an unknown dialect,
    /// or a resource it declares is already registered.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<JsonSchemaDocument> ParseAsync(Stream utf8Json, JsonSchemaDocumentOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        JsonElement element;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(utf8Json, _parseOptions, cancellationToken).ConfigureAwait(false);
            element = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw JsonSchemaProcessingException.SchemaJsonInvalid(exception);
        }

        return Create(element, options);
    }

    /// <summary>
    /// Loads a schema document from a JSON value. The value is copied, so the document does not
    /// depend on the lifetime of the <see cref="JsonDocument"/> it came from.
    /// </summary>
    /// <param name="schema">The schema's root value.</param>
    /// <param name="options">The load options, or <see langword="null"/> for <see cref="JsonSchemaDocumentOptions.Default"/>.</param>
    /// <returns>The document.</returns>
    /// <exception cref="JsonSchemaException">
    /// The schema is malformed, its <c>$schema</c> names an unknown dialect, a resource it declares is
    /// already registered, or <see cref="JsonSchemaDocumentOptions.ValidateSchema"/> is set and the
    /// schema does not conform to its meta-schema.
    /// </exception>
    public static JsonSchemaDocument Create(JsonElement schema, JsonSchemaDocumentOptions? options = null)
    {
        options ??= JsonSchemaDocumentOptions.Default;
        JsonSchemaRegistry registry = options.Registry ?? new JsonSchemaRegistry();
        JsonElement source = schema.ValueKind == JsonValueKind.Undefined ? schema : schema.Clone();
        JsonSchemaDocument document = new(source, registry, options.RegexMatchTimeout);

        UriReference initialBase;
        bool synthetic = false;
        if (options.BaseUri is Uri baseUri)
        {
            if (!UriReference.TryParse(baseUri.OriginalString, out UriReference? parsed) || !parsed.IsAbsolute)
            {
                if (!UriReference.TryParse(baseUri.AbsoluteUri, out parsed) || !parsed.IsAbsolute)
                {
                    throw new ArgumentException(ErrorMessages.UriNotAbsolute, nameof(options));
                }
            }

            initialBase = parsed.WithFragment(null);
        }
        else
        {
            UriReference.TryParse("urn:uuid:" + Guid.NewGuid().ToString("D"), out UriReference? generated);
            initialBase = generated!;
            synthetic = true;
        }

        SchemaCompiler compiler = new(document, registry, options.RegexMatchTimeout);
        document._root = compiler.CompileRoot(source, initialBase, synthetic, options.DefaultDialect ?? JsonSchemaDialect.Draft202012);
        List<KeyValuePair<string, SchemaResource>> entries = document.Publish(compiler, synthetic ? null : initialBase.ToNormalizedString(includeFragment: false));

        if (options.ValidateSchema)
        {
            document.ValidateAgainstMetaSchema();
        }

        registry.AddResources(entries);
        return document;
    }

    /// <summary>
    /// Finds the schema at a location within the document.
    /// </summary>
    /// <param name="location">The location of the schema.</param>
    /// <param name="schema">The schema when the location holds an object or boolean.</param>
    /// <returns><see langword="true"/> when the location holds a schema.</returns>
    /// <exception cref="ArgumentException"><paramref name="location"/> is <see langword="default"/>.</exception>
    public bool TryGetSchema(JsonPointer location, [NotNullWhen(true)] out IJsonSchema? schema)
    {
        if (location.Value is null)
        {
            throw new ArgumentException(ErrorMessages.JsonPointerDefault, nameof(location));
        }

        schema = GetOrCompileNode(location.Value);
        return schema is not null;
    }

    /// <inheritdoc/>
    public JsonSchemaEvaluationResult Evaluate(JsonElement instance, JsonSchemaEvaluationOptions? options = null) =>
        _root.Evaluate(instance, options);

    /// <inheritdoc/>
    public bool IsValid(JsonElement instance, JsonSchemaEvaluationOptions? options = null) =>
        _root.IsValid(instance, options);

    internal bool TryGetLocalResource(string uri, [NotNullWhen(true)] out SchemaResource? resource) =>
        _localResources.TryGetValue(uri, out resource);

    /// <summary>
    /// Resolves a URI fragment against a resource of this document: empty for the resource root, a
    /// JSON Pointer relative to it, or a plain-name anchor declared in it.
    /// </summary>
    internal SchemaNode? ResolveFragment(SchemaResource resource, string? fragment)
    {
        if (string.IsNullOrEmpty(fragment))
        {
            return resource.Root;
        }

        string? decoded = UriReference.PercentDecode(fragment);
        if (decoded is null)
        {
            return null;
        }

        if (decoded.Length > 0 && decoded[0] == '/')
        {
            return JsonPointer.IsValid(decoded, out _) ? GetOrCompileNode(resource.DocumentPointer + decoded) : null;
        }

        return resource.Anchors.TryGetValue(decoded, out SchemaNode? node) ? node : null;
    }

    /// <summary>
    /// Returns the schema at a document location, compiling it on first use when it is not a known
    /// subschema, such as a value inside an unknown keyword that a JSON Pointer reference names.
    /// </summary>
    internal SchemaNode? GetOrCompileNode(string pointer)
    {
        if (_nodes.TryGetValue(pointer, out SchemaNode? node))
        {
            return node;
        }

        if (!JsonPointer.CreateUnchecked(pointer).TryEvaluate(Source, out JsonElement element)
            || element.ValueKind is not (JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        lock (_compileGate)
        {
            if (_nodes.TryGetValue(pointer, out node))
            {
                return node;
            }

            SchemaCompiler compiler = new(this, Registry, _regexMatchTimeout);
            node = compiler.CompileDetached(element, pointer, FindEnclosingResource(pointer));
            foreach (SchemaResource resource in compiler.Resources)
            {
                // Identifiers discovered on demand stay private to this document.
                _localResources.TryAdd(resource.BaseText, resource);
            }

            foreach (SchemaNode compiled in compiler.Nodes)
            {
                _nodes.TryAdd(compiled.Pointer, compiled);
            }

            return node;
        }
    }

    /// <summary>
    /// Formats a document location for an exception: the document's base URI, when it has one, and the
    /// pointer as a fragment.
    /// </summary>
    internal string FormatLocation(string pointer)
    {
        string fragment = "#" + UriReference.EscapeFragment(pointer);
        SchemaNode? root = _root;
        return root is null || root.Resource.IsSynthetic ? fragment : root.Resource.BaseText + fragment;
    }

    private SchemaResource FindEnclosingResource(string pointer)
    {
        string current = pointer;
        while (current.Length > 0)
        {
            current = current.Substring(0, current.LastIndexOf('/'));
            if (_nodes.TryGetValue(current, out SchemaNode? ancestor))
            {
                return ancestor.Resource;
            }
        }

        return _root.Resource;
    }

    private List<KeyValuePair<string, SchemaResource>> Publish(SchemaCompiler compiler, string? retrievalUri)
    {
        List<KeyValuePair<string, SchemaResource>> entries = new();
        foreach (SchemaResource resource in compiler.Resources)
        {
            if (!_localResources.TryAdd(resource.BaseText, resource))
            {
                throw JsonSchemaProcessingException.ResourceDuplicate(resource.BaseText);
            }

            if (!resource.IsSynthetic)
            {
                entries.Add(new KeyValuePair<string, SchemaResource>(resource.BaseText, resource));
            }
        }

        // The document stays reachable at the URI it was loaded from when its root $id names another.
        if (retrievalUri is not null && !_localResources.ContainsKey(retrievalUri))
        {
            _localResources.TryAdd(retrievalUri, _root.Resource);
            entries.Add(new KeyValuePair<string, SchemaResource>(retrievalUri, _root.Resource));
        }

        foreach (SchemaNode node in compiler.Nodes)
        {
            _nodes.TryAdd(node.Pointer, node);
        }

        return entries;
    }

    private void ValidateAgainstMetaSchema()
    {
        JsonSchemaDialect dialect = Dialect;
        string metaSchema = dialect.IdText;
        if (!Registry.TryGetResource(metaSchema, JsonSchemaDialect.Draft202012, out SchemaResource? resource))
        {
            throw JsonSchemaProcessingException.ReferenceUnresolved(dialect.Id.OriginalString, FormatLocation(string.Empty));
        }

        if (!resource.Root.IsValid(Source))
        {
            throw JsonSchemaProcessingException.MetaSchemaViolation(dialect.Id.OriginalString, FormatLocation(string.Empty));
        }
    }

    private sealed class DebugView
    {
        private readonly JsonSchemaDocument _document;

        public DebugView(JsonSchemaDocument document)
        {
            _document = document;
        }

        public Uri? BaseUri => _document.BaseUri;

        public JsonSchemaDialect Dialect => _document.Dialect;

        public JsonElement Source => _document.Source;

        public JsonSchemaRegistry Registry => _document.Registry;
    }
}
