using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// A JSON Schema dialect: the vocabularies a schema is processed with, identified by the meta-schema
/// URI a schema names in <c>$schema</c>.
/// </summary>
/// <remarks>
/// <para>
/// The five specification dialects are built in. A schema whose <c>$schema</c> names another
/// meta-schema gets a dialect built from that meta-schema's <c>$vocabulary</c>; a dialect created with
/// <see cref="Create(Uri, IEnumerable{JsonSchemaVocabulary})"/> and registered with
/// <see cref="JsonSchemaRegistry.Register(JsonSchemaDialect)"/> takes precedence over both.
/// </para>
/// <para>
/// Keywords the dialect does not define are annotations in 2019-09 and later and are ignored in
/// earlier drafts.
/// </para>
/// </remarks>
[DebuggerDisplay("{Id,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonSchemaDialect
{
    private readonly Dictionary<string, JsonSchemaKeywordDefinition> _keywords;

    private JsonSchemaDialect(Uri id, JsonSchemaVocabulary[] vocabularies, JsonSchemaVersion version, Dictionary<string, JsonSchemaKeywordDefinition> keywords)
    {
        Id = id;
        Vocabularies = vocabularies;
        Version = version;
        _keywords = keywords;
        IdText = JsonSchemaVocabulary.Normalize(id);
    }

    /// <summary>Gets the draft 4 dialect, <c>http://json-schema.org/draft-04/schema#</c>.</summary>
    public static JsonSchemaDialect Draft04 { get; } = CreateBuiltIn("http://json-schema.org/draft-04/schema#", JsonSchemaVocabulary.Draft04);

    /// <summary>Gets the draft 6 dialect, <c>http://json-schema.org/draft-06/schema#</c>.</summary>
    public static JsonSchemaDialect Draft06 { get; } = CreateBuiltIn("http://json-schema.org/draft-06/schema#", JsonSchemaVocabulary.Draft06);

    /// <summary>Gets the draft 7 dialect, <c>http://json-schema.org/draft-07/schema#</c>.</summary>
    public static JsonSchemaDialect Draft07 { get; } = CreateBuiltIn("http://json-schema.org/draft-07/schema#", JsonSchemaVocabulary.Draft07);

    /// <summary>Gets the draft 2019-09 dialect, <c>https://json-schema.org/draft/2019-09/schema</c>.</summary>
    public static JsonSchemaDialect Draft201909 { get; } = CreateBuiltIn(
        "https://json-schema.org/draft/2019-09/schema",
        JsonSchemaVocabulary.Draft201909Core,
        JsonSchemaVocabulary.Draft201909Applicator,
        JsonSchemaVocabulary.Draft201909Validation,
        JsonSchemaVocabulary.Draft201909MetaData,
        JsonSchemaVocabulary.Draft201909Format,
        JsonSchemaVocabulary.Draft201909Content);

    /// <summary>Gets the draft 2020-12 dialect, <c>https://json-schema.org/draft/2020-12/schema</c>.</summary>
    public static JsonSchemaDialect Draft202012 { get; } = CreateBuiltIn(
        "https://json-schema.org/draft/2020-12/schema",
        JsonSchemaVocabulary.Draft202012Core,
        JsonSchemaVocabulary.Draft202012Applicator,
        JsonSchemaVocabulary.Draft202012Unevaluated,
        JsonSchemaVocabulary.Draft202012Validation,
        JsonSchemaVocabulary.Draft202012MetaData,
        JsonSchemaVocabulary.Draft202012FormatAnnotation,
        JsonSchemaVocabulary.Draft202012Content);

    /// <summary>
    /// Gets the meta-schema URI that identifies the dialect.
    /// </summary>
    public Uri Id { get; }

    /// <summary>
    /// Gets the specification version whose core semantics the dialect follows, taken from its core
    /// vocabulary.
    /// </summary>
    public JsonSchemaVersion Version { get; }

    /// <summary>
    /// Gets the dialect's vocabularies.
    /// </summary>
    public IReadOnlyList<JsonSchemaVocabulary> Vocabularies { get; }

    internal string IdText { get; }

    /// <summary>
    /// Creates a dialect from vocabularies.
    /// </summary>
    /// <param name="id">The meta-schema URI that identifies the dialect.</param>
    /// <param name="vocabularies">
    /// The vocabularies. Exactly one must be a core vocabulary: <see cref="JsonSchemaVocabulary.Draft202012Core"/>,
    /// <see cref="JsonSchemaVocabulary.Draft201909Core"/>, or one of the whole-draft vocabularies.
    /// </param>
    /// <returns>The dialect.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/>, <paramref name="vocabularies"/> or one of its items is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is not absolute, the vocabularies do not include exactly one core
    /// vocabulary, or two vocabularies define the same keyword.
    /// </exception>
    public static JsonSchemaDialect Create(Uri id, IEnumerable<JsonSchemaVocabulary> vocabularies)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(vocabularies);
        if (!id.IsAbsoluteUri)
        {
            throw new ArgumentException(ErrorMessages.UriNotAbsolute, nameof(id));
        }

        List<JsonSchemaVocabulary> list = new();
        foreach (JsonSchemaVocabulary vocabulary in vocabularies)
        {
            ArgumentNullException.ThrowIfNull(vocabulary, nameof(vocabularies));
            list.Add(vocabulary);
        }

        JsonSchemaVersion? version = null;
        foreach (JsonSchemaVocabulary vocabulary in list)
        {
            if (vocabulary.CoreVersion is JsonSchemaVersion core)
            {
                if (version is not null)
                {
                    throw new ArgumentException(ErrorMessages.DialectCoreVocabularyInvalid, nameof(vocabularies));
                }

                version = core;
            }
        }

        if (version is null)
        {
            throw new ArgumentException(ErrorMessages.DialectCoreVocabularyInvalid, nameof(vocabularies));
        }

        return Build(id, list, version.Value, nameof(vocabularies));
    }

    /// <summary>
    /// Finds the definition of a keyword in the dialect's vocabularies.
    /// </summary>
    /// <param name="name">The keyword's name.</param>
    /// <param name="definition">The definition when the dialect defines the keyword.</param>
    /// <returns><see langword="true"/> when the dialect defines the keyword.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryGetKeyword(string name, [NotNullWhen(true)] out JsonSchemaKeywordDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _keywords.TryGetValue(name, out definition);
    }

    /// <summary>
    /// Builds the dialect a meta-schema's <c>$vocabulary</c> describes. A meta-schema that lists no
    /// core vocabulary, which the specification forbids, keeps the core semantics of its own dialect.
    /// </summary>
    internal static JsonSchemaDialect FromVocabularies(Uri id, List<JsonSchemaVocabulary> vocabularies, JsonSchemaDialect metaSchemaDialect)
    {
        JsonSchemaVersion? version = null;
        foreach (JsonSchemaVocabulary vocabulary in vocabularies)
        {
            version ??= vocabulary.CoreVersion;
        }

        if (version is null)
        {
            foreach (JsonSchemaVocabulary vocabulary in metaSchemaDialect.Vocabularies)
            {
                if (vocabulary.CoreVersion is not null)
                {
                    vocabularies.Insert(0, vocabulary);
                    version = vocabulary.CoreVersion;
                    break;
                }
            }
        }

        return Build(id, vocabularies, version ?? metaSchemaDialect.Version, nameof(vocabularies));
    }

    internal bool Matches(string normalizedUri) => string.Equals(IdText, normalizedUri, StringComparison.Ordinal);

    private static JsonSchemaDialect CreateBuiltIn(string id, params JsonSchemaVocabulary[] vocabularies)
    {
        JsonSchemaVersion version = JsonSchemaVersion.Draft202012;
        foreach (JsonSchemaVocabulary vocabulary in vocabularies)
        {
            if (vocabulary.CoreVersion is JsonSchemaVersion core)
            {
                version = core;
            }
        }

        return Build(new Uri(id, UriKind.Absolute), new List<JsonSchemaVocabulary>(vocabularies), version, nameof(vocabularies));
    }

    private static JsonSchemaDialect Build(Uri id, List<JsonSchemaVocabulary> vocabularies, JsonSchemaVersion version, string parameterName)
    {
        Dictionary<string, JsonSchemaKeywordDefinition> keywords = new(StringComparer.Ordinal);
        foreach (JsonSchemaVocabulary vocabulary in vocabularies)
        {
            foreach (JsonSchemaKeywordDefinition definition in vocabulary.Keywords)
            {
                if (keywords.TryGetValue(definition.Name, out JsonSchemaKeywordDefinition? existing))
                {
                    if (IsFormat(existing) && IsFormat(definition))
                    {
                        // Format-annotation and format-assertion both define "format"; assertion wins.
                        if (definition.Kind == KeywordKind.FormatAssertion)
                        {
                            keywords[definition.Name] = definition;
                        }

                        continue;
                    }

                    if (IsCompatibilityKeyword(existing) && IsCompatibilityKeyword(definition))
                    {
                        continue;
                    }

                    throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.KeywordDuplicate, definition.Name), parameterName);
                }

                keywords.Add(definition.Name, definition);
            }
        }

        return new JsonSchemaDialect(id, vocabularies.ToArray(), version, keywords);
    }

    private static bool IsFormat(JsonSchemaKeywordDefinition definition) =>
        definition.Kind is KeywordKind.Format or KeywordKind.FormatAnnotation or KeywordKind.FormatAssertion;

    private static bool IsCompatibilityKeyword(JsonSchemaKeywordDefinition definition) =>
        definition.Kind is KeywordKind.Defs or KeywordKind.Dependencies;

    private sealed class DebugView
    {
        private readonly JsonSchemaDialect _dialect;

        public DebugView(JsonSchemaDialect dialect)
        {
            _dialect = dialect;
        }

        public Uri Id => _dialect.Id;

        public JsonSchemaVersion Version => _dialect.Version;

        public IReadOnlyList<JsonSchemaVocabulary> Vocabularies => _dialect.Vocabularies;
    }
}
