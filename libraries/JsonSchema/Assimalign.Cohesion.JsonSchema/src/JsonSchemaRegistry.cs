using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// The schema resources, dialects and vocabularies that references, <c>$schema</c> and
/// <c>$vocabulary</c> resolve against.
/// </summary>
/// <remarks>
/// <para>
/// Every document registers the schema resources it declares in its registry when it loads: its root
/// under its base URI and, when the root's <c>$id</c> differs, also under the URI it was loaded from,
/// and every embedded resource under its <c>$id</c>. A reference to a resource the registry does not
/// hold is resolved through <see cref="Resolver"/>; the official meta-schemas of the five built-in
/// dialects are always available without one.
/// </para>
/// <para>
/// A registry is thread-safe. Registration is atomic: a document whose resources collide with
/// registered ones is rejected whole.
/// </para>
/// </remarks>
[DebuggerDisplay("Resources = {ResourceCount}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonSchemaRegistry
{
    private readonly object _gate = new();
    private readonly bool _isBuiltIn;
    private ImmutableDictionary<string, SchemaResource> _resources = ImmutableDictionary.Create<string, SchemaResource>(StringComparer.Ordinal);
    private ImmutableDictionary<string, JsonSchemaDialect> _dialects = ImmutableDictionary.Create<string, JsonSchemaDialect>(StringComparer.Ordinal);
    private ImmutableDictionary<string, JsonSchemaDialect> _derivedDialects = ImmutableDictionary.Create<string, JsonSchemaDialect>(StringComparer.Ordinal);
    private ImmutableDictionary<string, JsonSchemaVocabulary> _vocabularies = ImmutableDictionary.Create<string, JsonSchemaVocabulary>(StringComparer.Ordinal);

    /// <summary>
    /// Initializes an empty registry.
    /// </summary>
    public JsonSchemaRegistry()
    {
    }

    private JsonSchemaRegistry(bool isBuiltIn)
    {
        _isBuiltIn = isBuiltIn;
    }

    /// <summary>
    /// Gets the resolver that supplies documents the registry does not hold, or
    /// <see langword="null"/> for none.
    /// </summary>
    public IJsonSchemaResolver? Resolver { get; init; }

    /// <summary>
    /// Gets the registry of the official meta-schemas, which every registry falls back to.
    /// </summary>
    internal static JsonSchemaRegistry BuiltIn { get; } = new(isBuiltIn: true) { Resolver = MetaSchemaResolver.Instance };

    internal int ResourceCount => Volatile.Read(ref _resources).Count;

    /// <summary>
    /// Loads a schema document and registers it under a URI.
    /// </summary>
    /// <param name="uri">The URI the document is known by: its initial base URI.</param>
    /// <param name="schema">The document's root value.</param>
    /// <param name="defaultDialect">
    /// The dialect for a document whose root has no <c>$schema</c>, or <see langword="null"/> for
    /// <see cref="JsonSchemaDialect.Draft202012"/>.
    /// </param>
    /// <returns>The loaded document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is not absolute.</exception>
    /// <exception cref="JsonSchemaException">
    /// The document cannot be loaded, or a resource it declares is already registered
    /// (<see cref="JsonSchemaErrorCode.DuplicateResource"/>).
    /// </exception>
    public JsonSchemaDocument Register(Uri uri, JsonElement schema, JsonSchemaDialect? defaultDialect = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException(ErrorMessages.UriNotAbsolute, nameof(uri));
        }

        return JsonSchemaDocument.Create(schema, new JsonSchemaDocumentOptions
        {
            BaseUri = uri,
            DefaultDialect = defaultDialect,
            Registry = this,
        });
    }

    /// <summary>
    /// Registers a dialect, so schemas whose <c>$schema</c> names its <see cref="JsonSchemaDialect.Id"/>
    /// use it.
    /// </summary>
    /// <param name="dialect">The dialect.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dialect"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonSchemaException">A different dialect is already registered under the same identifier (<see cref="JsonSchemaErrorCode.DuplicateResource"/>).</exception>
    public void Register(JsonSchemaDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        lock (_gate)
        {
            if (_dialects.TryGetValue(dialect.IdText, out JsonSchemaDialect? existing) && !ReferenceEquals(existing, dialect))
            {
                throw JsonSchemaProcessingException.ResourceDuplicate(dialect.IdText);
            }

            Volatile.Write(ref _dialects, _dialects.SetItem(dialect.IdText, dialect));
        }
    }

    /// <summary>
    /// Registers a vocabulary, so meta-schemas that name its <see cref="JsonSchemaVocabulary.Id"/> in
    /// <c>$vocabulary</c> include its keywords.
    /// </summary>
    /// <param name="vocabulary">The vocabulary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vocabulary"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonSchemaException">A different vocabulary is already registered under the same identifier (<see cref="JsonSchemaErrorCode.DuplicateResource"/>).</exception>
    public void Register(JsonSchemaVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        lock (_gate)
        {
            if (_vocabularies.TryGetValue(vocabulary.IdText, out JsonSchemaVocabulary? existing) && !ReferenceEquals(existing, vocabulary))
            {
                throw JsonSchemaProcessingException.ResourceDuplicate(vocabulary.IdText);
            }

            Volatile.Write(ref _vocabularies, _vocabularies.SetItem(vocabulary.IdText, vocabulary));
        }
    }

    /// <summary>
    /// Finds the schema a URI identifies: a resource, or a location within one named by a JSON Pointer
    /// or anchor fragment. A resource the registry does not hold is requested from
    /// <see cref="Resolver"/>, and loaded as draft 2020-12 when it has no <c>$schema</c>.
    /// </summary>
    /// <param name="uri">The absolute URI.</param>
    /// <param name="schema">The schema when it is found.</param>
    /// <returns><see langword="true"/> when the schema was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonSchemaException">A document supplied by the resolver cannot be loaded.</exception>
    public bool TryGetSchema(Uri uri, [NotNullWhen(true)] out IJsonSchema? schema)
    {
        ArgumentNullException.ThrowIfNull(uri);
        schema = null;
        if (!UriReference.TryParse(uri.OriginalString, out UriReference? reference) || !reference.IsAbsolute)
        {
            return false;
        }

        if (!TryGetResource(reference.ToNormalizedString(includeFragment: false), JsonSchemaDialect.Draft202012, out SchemaResource? resource))
        {
            return false;
        }

        SchemaNode? node = resource.Document.ResolveFragment(resource, reference.Fragment);
        schema = node;
        return node is not null;
    }

    /// <summary>
    /// Finds a registered resource, then a built-in meta-schema, then asks the resolver.
    /// </summary>
    internal bool TryGetResource(string uri, JsonSchemaDialect defaultDialect, [NotNullWhen(true)] out SchemaResource? resource)
    {
        if (Volatile.Read(ref _resources).TryGetValue(uri, out resource))
        {
            return true;
        }

        if (!_isBuiltIn && BuiltIn.TryGetResource(uri, defaultDialect, out resource))
        {
            return true;
        }

        IJsonSchemaResolver? resolver = Resolver;
        if (resolver is null || !Uri.TryCreate(uri, UriKind.Absolute, out Uri? address) || !resolver.TryResolve(address, out JsonElement element))
        {
            return false;
        }

        try
        {
            Register(address, element, defaultDialect);
        }
        catch (JsonSchemaException exception) when (exception.Code == JsonSchemaErrorCode.DuplicateResource && Volatile.Read(ref _resources).ContainsKey(uri))
        {
            // Another thread loaded the same document first; use its copy.
        }

        return Volatile.Read(ref _resources).TryGetValue(uri, out resource);
    }

    /// <summary>
    /// Registers a document's resources atomically, or none of them.
    /// </summary>
    internal void AddResources(List<KeyValuePair<string, SchemaResource>> entries)
    {
        lock (_gate)
        {
            ImmutableDictionary<string, SchemaResource> current = _resources;
            foreach (KeyValuePair<string, SchemaResource> entry in entries)
            {
                if (current.ContainsKey(entry.Key))
                {
                    throw JsonSchemaProcessingException.ResourceDuplicate(entry.Key);
                }
            }

            Volatile.Write(ref _resources, current.AddRange(entries));
        }
    }

    /// <summary>
    /// Resolves the dialect a <c>$schema</c> value names: a built-in dialect, a registered one, or the
    /// dialect a meta-schema's <c>$vocabulary</c> describes.
    /// </summary>
    internal JsonSchemaDialect ResolveDialect(string metaSchema, string? location)
    {
        if (!UriReference.TryParse(metaSchema, out UriReference? reference) || !reference.IsAbsolute)
        {
            throw JsonSchemaProcessingException.DialectUnknown(metaSchema, location);
        }

        string key = reference.ToNormalizedString(includeFragment: !string.IsNullOrEmpty(reference.Fragment));
        if (TryGetKnownDialect(key, out JsonSchemaDialect? dialect))
        {
            return dialect;
        }

        if (Volatile.Read(ref _derivedDialects).TryGetValue(key, out dialect))
        {
            return dialect;
        }

        if (!string.IsNullOrEmpty(reference.Fragment)
            || !TryGetResource(key, JsonSchemaDialect.Draft202012, out SchemaResource? resource))
        {
            throw JsonSchemaProcessingException.DialectUnknown(metaSchema, location);
        }

        JsonElement root = resource.Root.Element;
        JsonSchemaDialect metaSchemaDialect = resource.Dialect;
        if (metaSchemaDialect.Version >= JsonSchemaVersion.Draft201909
            && root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("$vocabulary", out JsonElement vocabularyMap)
            && vocabularyMap.ValueKind == JsonValueKind.Object)
        {
            List<JsonSchemaVocabulary> vocabularies = new();
            foreach (JsonProperty entry in vocabularyMap.EnumerateObject())
            {
                JsonSchemaVocabulary? vocabulary = FindVocabulary(entry.Name);
                if (vocabulary is null)
                {
                    if (entry.Value.ValueKind == JsonValueKind.True)
                    {
                        throw JsonSchemaProcessingException.VocabularyUnsupported(metaSchema, entry.Name);
                    }

                    continue;
                }

                if (!vocabularies.Contains(vocabulary))
                {
                    vocabularies.Add(vocabulary);
                }
            }

            try
            {
                dialect = JsonSchemaDialect.FromVocabularies(new Uri(key, UriKind.Absolute), vocabularies, metaSchemaDialect);
            }
            catch (ArgumentException)
            {
                // Vocabularies that redefine each other's keywords cannot form one dialect.
                throw JsonSchemaProcessingException.DialectUnknown(metaSchema, location);
            }
        }
        else
        {
            dialect = metaSchemaDialect;
        }

        lock (_gate)
        {
            if (_derivedDialects.TryGetValue(key, out JsonSchemaDialect? existing))
            {
                return existing;
            }

            Volatile.Write(ref _derivedDialects, _derivedDialects.SetItem(key, dialect));
        }

        return dialect;
    }

    private bool TryGetKnownDialect(string key, [NotNullWhen(true)] out JsonSchemaDialect? dialect)
    {
        foreach (JsonSchemaDialect builtIn in (ReadOnlySpan<JsonSchemaDialect>)[JsonSchemaDialect.Draft202012, JsonSchemaDialect.Draft201909, JsonSchemaDialect.Draft07, JsonSchemaDialect.Draft06, JsonSchemaDialect.Draft04])
        {
            if (builtIn.Matches(key))
            {
                dialect = builtIn;
                return true;
            }
        }

        return Volatile.Read(ref _dialects).TryGetValue(key, out dialect);
    }

    private JsonSchemaVocabulary? FindVocabulary(string id)
    {
        string key = UriReference.TryParse(id, out UriReference? reference) && reference.IsAbsolute
            ? reference.ToNormalizedString(includeFragment: !string.IsNullOrEmpty(reference.Fragment))
            : id;
        if (JsonSchemaVocabulary.TryGetBuiltIn(key, out JsonSchemaVocabulary? vocabulary))
        {
            return vocabulary;
        }

        return Volatile.Read(ref _vocabularies).TryGetValue(key, out vocabulary) ? vocabulary : null;
    }

    private sealed class DebugView
    {
        private readonly JsonSchemaRegistry _registry;

        public DebugView(JsonSchemaRegistry registry)
        {
            _registry = registry;
        }

        public IJsonSchemaResolver? Resolver => _registry.Resolver;
    }
}
