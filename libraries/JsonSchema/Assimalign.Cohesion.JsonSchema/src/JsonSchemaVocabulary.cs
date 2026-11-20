using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Assimalign.Cohesion.JsonSchema.Internal;

namespace Assimalign.Cohesion.JsonSchema;

/// <summary>
/// A named set of keywords (2020-12 Core §8.1.2). Dialects combine vocabularies, and meta-schemas
/// declare the vocabularies their schemas use with <c>$vocabulary</c>.
/// </summary>
/// <remarks>
/// <para>
/// Draft 2019-09 and 2020-12 define their vocabularies by URI. Drafts 4, 6 and 7 predate vocabularies,
/// so each is one vocabulary named by its meta-schema URI.
/// </para>
/// <para>
/// The 2019-09 and 2020-12 core vocabularies also define <c>definitions</c>, and their applicator
/// vocabularies define <c>dependencies</c>: both left the specification but stay in its meta-schemas
/// because schemas still use them, so the library keeps their earlier meaning.
/// </para>
/// </remarks>
[DebuggerDisplay("{Id,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class JsonSchemaVocabulary
{
    private static readonly Dictionary<string, JsonSchemaVocabulary> _builtIn = new(StringComparer.Ordinal);

    private JsonSchemaVocabulary(Uri id, JsonSchemaKeywordDefinition[] keywords, JsonSchemaVersion? coreVersion)
    {
        Id = id;
        Keywords = keywords;
        CoreVersion = coreVersion;
        IdText = Normalize(id);
    }

    /// <summary>
    /// Gets the vocabulary's identifier.
    /// </summary>
    public Uri Id { get; }

    /// <summary>
    /// Gets the keywords the vocabulary defines.
    /// </summary>
    public IReadOnlyList<JsonSchemaKeywordDefinition> Keywords { get; }

    internal string IdText { get; }

    /// <summary>
    /// Gets the specification version whose core semantics this vocabulary carries, or
    /// <see langword="null"/> when it is not a core vocabulary.
    /// </summary>
    internal JsonSchemaVersion? CoreVersion { get; }

    /// <summary>Gets the whole of draft 4, which predates vocabularies.</summary>
    public static JsonSchemaVocabulary Draft04 { get; } = BuiltIn("http://json-schema.org/draft-04/schema", JsonSchemaVersion.Draft04, CreateDraft04());

    /// <summary>Gets the whole of draft 6, which predates vocabularies.</summary>
    public static JsonSchemaVocabulary Draft06 { get; } = BuiltIn("http://json-schema.org/draft-06/schema", JsonSchemaVersion.Draft06, CreateDraft06(draft07: false));

    /// <summary>Gets the whole of draft 7, which predates vocabularies.</summary>
    public static JsonSchemaVocabulary Draft07 { get; } = BuiltIn("http://json-schema.org/draft-07/schema", JsonSchemaVersion.Draft07, CreateDraft06(draft07: true));

    /// <summary>Gets the draft 2019-09 core vocabulary.</summary>
    public static JsonSchemaVocabulary Draft201909Core { get; } = BuiltIn("https://json-schema.org/draft/2019-09/vocab/core", JsonSchemaVersion.Draft201909,
    [
        Keyword("$schema", KeywordKind.Schema),
        Keyword("$id", KeywordKind.Id),
        Keyword("$anchor", KeywordKind.Anchor),
        Keyword("$ref", KeywordKind.Ref),
        Keyword("$recursiveRef", KeywordKind.RecursiveRef),
        Keyword("$recursiveAnchor", KeywordKind.RecursiveAnchor),
        Keyword("$vocabulary", KeywordKind.Vocabulary),
        Keyword("$comment", KeywordKind.Comment),
        Keyword("$defs", KeywordKind.Defs, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("definitions", KeywordKind.Defs, JsonSchemaSubschemaLayout.SchemaMap),
    ]);

    /// <summary>Gets the draft 2019-09 applicator vocabulary.</summary>
    public static JsonSchemaVocabulary Draft201909Applicator { get; } = BuiltIn("https://json-schema.org/draft/2019-09/vocab/applicator", null,
    [
        Keyword("additionalItems", KeywordKind.AdditionalItems, JsonSchemaSubschemaLayout.Schema),
        Keyword("unevaluatedItems", KeywordKind.UnevaluatedItems, JsonSchemaSubschemaLayout.Schema),
        Keyword("items", KeywordKind.ItemsLegacy, JsonSchemaSubschemaLayout.SchemaOrSchemaArray),
        Keyword("contains", KeywordKind.ContainsLegacy, JsonSchemaSubschemaLayout.Schema),
        Keyword("additionalProperties", KeywordKind.AdditionalProperties, JsonSchemaSubschemaLayout.Schema),
        Keyword("unevaluatedProperties", KeywordKind.UnevaluatedProperties, JsonSchemaSubschemaLayout.Schema),
        Keyword("properties", KeywordKind.Properties, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("patternProperties", KeywordKind.PatternProperties, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("dependentSchemas", KeywordKind.DependentSchemas, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("dependencies", KeywordKind.Dependencies),
        Keyword("propertyNames", KeywordKind.PropertyNames, JsonSchemaSubschemaLayout.Schema),
        Keyword("if", KeywordKind.If, JsonSchemaSubschemaLayout.Schema),
        Keyword("then", KeywordKind.Then, JsonSchemaSubschemaLayout.Schema),
        Keyword("else", KeywordKind.Else, JsonSchemaSubschemaLayout.Schema),
        Keyword("allOf", KeywordKind.AllOf, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("anyOf", KeywordKind.AnyOf, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("oneOf", KeywordKind.OneOf, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("not", KeywordKind.Not, JsonSchemaSubschemaLayout.Schema),
    ]);

    /// <summary>Gets the draft 2019-09 validation vocabulary.</summary>
    public static JsonSchemaVocabulary Draft201909Validation { get; } = BuiltIn("https://json-schema.org/draft/2019-09/vocab/validation", null, CreateValidation());

    /// <summary>Gets the draft 2019-09 meta-data vocabulary.</summary>
    public static JsonSchemaVocabulary Draft201909MetaData { get; } = BuiltIn("https://json-schema.org/draft/2019-09/vocab/meta-data", null, CreateMetaData());

    /// <summary>Gets the draft 2019-09 format vocabulary, which annotates unless assertion is enabled.</summary>
    public static JsonSchemaVocabulary Draft201909Format { get; } = BuiltIn("https://json-schema.org/draft/2019-09/vocab/format", null,
    [
        Keyword("format", KeywordKind.Format),
    ]);

    /// <summary>Gets the draft 2019-09 content vocabulary.</summary>
    public static JsonSchemaVocabulary Draft201909Content { get; } = BuiltIn("https://json-schema.org/draft/2019-09/vocab/content", null, CreateContent());

    /// <summary>Gets the draft 2020-12 core vocabulary.</summary>
    public static JsonSchemaVocabulary Draft202012Core { get; } = BuiltIn("https://json-schema.org/draft/2020-12/vocab/core", JsonSchemaVersion.Draft202012,
    [
        Keyword("$schema", KeywordKind.Schema),
        Keyword("$id", KeywordKind.Id),
        Keyword("$ref", KeywordKind.Ref),
        Keyword("$anchor", KeywordKind.Anchor),
        Keyword("$dynamicRef", KeywordKind.DynamicRef),
        Keyword("$dynamicAnchor", KeywordKind.DynamicAnchor),
        Keyword("$vocabulary", KeywordKind.Vocabulary),
        Keyword("$comment", KeywordKind.Comment),
        Keyword("$defs", KeywordKind.Defs, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("definitions", KeywordKind.Defs, JsonSchemaSubschemaLayout.SchemaMap),
    ]);

    /// <summary>Gets the draft 2020-12 applicator vocabulary.</summary>
    public static JsonSchemaVocabulary Draft202012Applicator { get; } = BuiltIn("https://json-schema.org/draft/2020-12/vocab/applicator", null,
    [
        Keyword("prefixItems", KeywordKind.PrefixItems, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("items", KeywordKind.Items, JsonSchemaSubschemaLayout.Schema),
        Keyword("contains", KeywordKind.Contains, JsonSchemaSubschemaLayout.Schema),
        Keyword("additionalProperties", KeywordKind.AdditionalProperties, JsonSchemaSubschemaLayout.Schema),
        Keyword("properties", KeywordKind.Properties, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("patternProperties", KeywordKind.PatternProperties, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("dependentSchemas", KeywordKind.DependentSchemas, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("dependencies", KeywordKind.Dependencies),
        Keyword("propertyNames", KeywordKind.PropertyNames, JsonSchemaSubschemaLayout.Schema),
        Keyword("if", KeywordKind.If, JsonSchemaSubschemaLayout.Schema),
        Keyword("then", KeywordKind.Then, JsonSchemaSubschemaLayout.Schema),
        Keyword("else", KeywordKind.Else, JsonSchemaSubschemaLayout.Schema),
        Keyword("allOf", KeywordKind.AllOf, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("anyOf", KeywordKind.AnyOf, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("oneOf", KeywordKind.OneOf, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("not", KeywordKind.Not, JsonSchemaSubschemaLayout.Schema),
    ]);

    /// <summary>Gets the draft 2020-12 unevaluated vocabulary.</summary>
    public static JsonSchemaVocabulary Draft202012Unevaluated { get; } = BuiltIn("https://json-schema.org/draft/2020-12/vocab/unevaluated", null,
    [
        Keyword("unevaluatedItems", KeywordKind.UnevaluatedItems, JsonSchemaSubschemaLayout.Schema),
        Keyword("unevaluatedProperties", KeywordKind.UnevaluatedProperties, JsonSchemaSubschemaLayout.Schema),
    ]);

    /// <summary>Gets the draft 2020-12 validation vocabulary.</summary>
    public static JsonSchemaVocabulary Draft202012Validation { get; } = BuiltIn("https://json-schema.org/draft/2020-12/vocab/validation", null, CreateValidation());

    /// <summary>Gets the draft 2020-12 meta-data vocabulary.</summary>
    public static JsonSchemaVocabulary Draft202012MetaData { get; } = BuiltIn("https://json-schema.org/draft/2020-12/vocab/meta-data", null, CreateMetaData());

    /// <summary>Gets the draft 2020-12 format-annotation vocabulary, which annotates unless assertion is enabled.</summary>
    public static JsonSchemaVocabulary Draft202012FormatAnnotation { get; } = BuiltIn("https://json-schema.org/draft/2020-12/vocab/format-annotation", null,
    [
        Keyword("format", KeywordKind.FormatAnnotation),
    ]);

    /// <summary>Gets the draft 2020-12 format-assertion vocabulary, which always asserts and fails unknown formats.</summary>
    public static JsonSchemaVocabulary Draft202012FormatAssertion { get; } = BuiltIn("https://json-schema.org/draft/2020-12/vocab/format-assertion", null,
    [
        Keyword("format", KeywordKind.FormatAssertion),
    ]);

    /// <summary>Gets the draft 2020-12 content vocabulary.</summary>
    public static JsonSchemaVocabulary Draft202012Content { get; } = BuiltIn("https://json-schema.org/draft/2020-12/vocab/content", null, CreateContent());

    /// <summary>
    /// Creates a custom vocabulary.
    /// </summary>
    /// <param name="id">The vocabulary's absolute URI, as meta-schemas name it in <c>$vocabulary</c>.</param>
    /// <param name="keywords">The keywords it defines.</param>
    /// <returns>The vocabulary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/>, <paramref name="keywords"/> or one of its items is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not absolute, or two keywords share a name.</exception>
    public static JsonSchemaVocabulary Create(Uri id, IEnumerable<JsonSchemaKeywordDefinition> keywords)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(keywords);
        if (!id.IsAbsoluteUri)
        {
            throw new ArgumentException(ErrorMessages.UriNotAbsolute, nameof(id));
        }

        List<JsonSchemaKeywordDefinition> definitions = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonSchemaKeywordDefinition definition in keywords)
        {
            ArgumentNullException.ThrowIfNull(definition, nameof(keywords));
            if (!names.Add(definition.Name))
            {
                throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.KeywordDuplicate, definition.Name), nameof(keywords));
            }

            definitions.Add(definition);
        }

        return new JsonSchemaVocabulary(id, definitions.ToArray(), null);
    }

    internal static bool TryGetBuiltIn(string id, [NotNullWhen(true)] out JsonSchemaVocabulary? vocabulary)
    {
        return _builtIn.TryGetValue(id, out vocabulary);
    }

    internal static string Normalize(Uri id)
    {
        string text = id.OriginalString;
        return UriReference.TryParse(text, out UriReference? reference) && reference.IsAbsolute
            ? reference.ToNormalizedString(includeFragment: !string.IsNullOrEmpty(reference.Fragment))
            : text;
    }

    private static JsonSchemaVocabulary BuiltIn(string id, JsonSchemaVersion? coreVersion, JsonSchemaKeywordDefinition[] keywords)
    {
        JsonSchemaVocabulary vocabulary = new(new Uri(id, UriKind.Absolute), keywords, coreVersion);
        _builtIn[vocabulary.IdText] = vocabulary;
        return vocabulary;
    }

    private static JsonSchemaKeywordDefinition Keyword(string name, KeywordKind kind, JsonSchemaSubschemaLayout layout = JsonSchemaSubschemaLayout.None) =>
        JsonSchemaKeywordDefinition.BuiltIn(name, kind, layout);

    private static JsonSchemaKeywordDefinition[] CreateDraft04() =>
    [
        Keyword("$schema", KeywordKind.Schema),
        Keyword("id", KeywordKind.Id),
        Keyword("$ref", KeywordKind.Ref),
        Keyword("definitions", KeywordKind.Defs, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("title", KeywordKind.Annotation),
        Keyword("description", KeywordKind.Annotation),
        Keyword("default", KeywordKind.Annotation),
        Keyword("multipleOf", KeywordKind.MultipleOf),
        Keyword("maximum", KeywordKind.MaximumDraft04),
        Keyword("exclusiveMaximum", KeywordKind.ExclusiveMaximumDraft04),
        Keyword("minimum", KeywordKind.MinimumDraft04),
        Keyword("exclusiveMinimum", KeywordKind.ExclusiveMinimumDraft04),
        Keyword("maxLength", KeywordKind.MaxLength),
        Keyword("minLength", KeywordKind.MinLength),
        Keyword("pattern", KeywordKind.Pattern),
        Keyword("additionalItems", KeywordKind.AdditionalItems, JsonSchemaSubschemaLayout.Schema),
        Keyword("items", KeywordKind.ItemsLegacy, JsonSchemaSubschemaLayout.SchemaOrSchemaArray),
        Keyword("maxItems", KeywordKind.MaxItems),
        Keyword("minItems", KeywordKind.MinItems),
        Keyword("uniqueItems", KeywordKind.UniqueItems),
        Keyword("maxProperties", KeywordKind.MaxProperties),
        Keyword("minProperties", KeywordKind.MinProperties),
        Keyword("required", KeywordKind.Required),
        Keyword("additionalProperties", KeywordKind.AdditionalProperties, JsonSchemaSubschemaLayout.Schema),
        Keyword("properties", KeywordKind.Properties, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("patternProperties", KeywordKind.PatternProperties, JsonSchemaSubschemaLayout.SchemaMap),
        Keyword("dependencies", KeywordKind.Dependencies),
        Keyword("enum", KeywordKind.Enum),
        Keyword("type", KeywordKind.Type),
        Keyword("format", KeywordKind.Format),
        Keyword("allOf", KeywordKind.AllOf, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("anyOf", KeywordKind.AnyOf, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("oneOf", KeywordKind.OneOf, JsonSchemaSubschemaLayout.SchemaArray),
        Keyword("not", KeywordKind.Not, JsonSchemaSubschemaLayout.Schema),
    ];

    private static JsonSchemaKeywordDefinition[] CreateDraft06(bool draft07)
    {
        List<JsonSchemaKeywordDefinition> keywords =
        [
            Keyword("$schema", KeywordKind.Schema),
            Keyword("$id", KeywordKind.Id),
            Keyword("$ref", KeywordKind.Ref),
            Keyword("definitions", KeywordKind.Defs, JsonSchemaSubschemaLayout.SchemaMap),
            Keyword("title", KeywordKind.Annotation),
            Keyword("description", KeywordKind.Annotation),
            Keyword("default", KeywordKind.Annotation),
            Keyword("examples", KeywordKind.Annotation),
            Keyword("multipleOf", KeywordKind.MultipleOf),
            Keyword("maximum", KeywordKind.Maximum),
            Keyword("exclusiveMaximum", KeywordKind.ExclusiveMaximum),
            Keyword("minimum", KeywordKind.Minimum),
            Keyword("exclusiveMinimum", KeywordKind.ExclusiveMinimum),
            Keyword("maxLength", KeywordKind.MaxLength),
            Keyword("minLength", KeywordKind.MinLength),
            Keyword("pattern", KeywordKind.Pattern),
            Keyword("additionalItems", KeywordKind.AdditionalItems, JsonSchemaSubschemaLayout.Schema),
            Keyword("items", KeywordKind.ItemsLegacy, JsonSchemaSubschemaLayout.SchemaOrSchemaArray),
            Keyword("maxItems", KeywordKind.MaxItems),
            Keyword("minItems", KeywordKind.MinItems),
            Keyword("uniqueItems", KeywordKind.UniqueItems),
            Keyword("contains", KeywordKind.ContainsLegacy, JsonSchemaSubschemaLayout.Schema),
            Keyword("maxProperties", KeywordKind.MaxProperties),
            Keyword("minProperties", KeywordKind.MinProperties),
            Keyword("required", KeywordKind.Required),
            Keyword("additionalProperties", KeywordKind.AdditionalProperties, JsonSchemaSubschemaLayout.Schema),
            Keyword("properties", KeywordKind.Properties, JsonSchemaSubschemaLayout.SchemaMap),
            Keyword("patternProperties", KeywordKind.PatternProperties, JsonSchemaSubschemaLayout.SchemaMap),
            Keyword("dependencies", KeywordKind.Dependencies),
            Keyword("propertyNames", KeywordKind.PropertyNames, JsonSchemaSubschemaLayout.Schema),
            Keyword("const", KeywordKind.Const),
            Keyword("enum", KeywordKind.Enum),
            Keyword("type", KeywordKind.Type),
            Keyword("format", KeywordKind.Format),
            Keyword("allOf", KeywordKind.AllOf, JsonSchemaSubschemaLayout.SchemaArray),
            Keyword("anyOf", KeywordKind.AnyOf, JsonSchemaSubschemaLayout.SchemaArray),
            Keyword("oneOf", KeywordKind.OneOf, JsonSchemaSubschemaLayout.SchemaArray),
            Keyword("not", KeywordKind.Not, JsonSchemaSubschemaLayout.Schema),
        ];

        if (draft07)
        {
            keywords.Add(Keyword("$comment", KeywordKind.Comment));
            keywords.Add(Keyword("readOnly", KeywordKind.Annotation));
            keywords.Add(Keyword("writeOnly", KeywordKind.Annotation));
            keywords.Add(Keyword("contentMediaType", KeywordKind.ContentMediaType));
            keywords.Add(Keyword("contentEncoding", KeywordKind.ContentEncoding));
            keywords.Add(Keyword("if", KeywordKind.If, JsonSchemaSubschemaLayout.Schema));
            keywords.Add(Keyword("then", KeywordKind.Then, JsonSchemaSubschemaLayout.Schema));
            keywords.Add(Keyword("else", KeywordKind.Else, JsonSchemaSubschemaLayout.Schema));
        }

        return keywords.ToArray();
    }

    private static JsonSchemaKeywordDefinition[] CreateValidation() =>
    [
        Keyword("multipleOf", KeywordKind.MultipleOf),
        Keyword("maximum", KeywordKind.Maximum),
        Keyword("exclusiveMaximum", KeywordKind.ExclusiveMaximum),
        Keyword("minimum", KeywordKind.Minimum),
        Keyword("exclusiveMinimum", KeywordKind.ExclusiveMinimum),
        Keyword("maxLength", KeywordKind.MaxLength),
        Keyword("minLength", KeywordKind.MinLength),
        Keyword("pattern", KeywordKind.Pattern),
        Keyword("maxItems", KeywordKind.MaxItems),
        Keyword("minItems", KeywordKind.MinItems),
        Keyword("uniqueItems", KeywordKind.UniqueItems),
        Keyword("maxContains", KeywordKind.MaxContains),
        Keyword("minContains", KeywordKind.MinContains),
        Keyword("maxProperties", KeywordKind.MaxProperties),
        Keyword("minProperties", KeywordKind.MinProperties),
        Keyword("required", KeywordKind.Required),
        Keyword("dependentRequired", KeywordKind.DependentRequired),
        Keyword("const", KeywordKind.Const),
        Keyword("enum", KeywordKind.Enum),
        Keyword("type", KeywordKind.Type),
    ];

    private static JsonSchemaKeywordDefinition[] CreateMetaData() =>
    [
        Keyword("title", KeywordKind.Annotation),
        Keyword("description", KeywordKind.Annotation),
        Keyword("default", KeywordKind.Annotation),
        Keyword("deprecated", KeywordKind.Annotation),
        Keyword("readOnly", KeywordKind.Annotation),
        Keyword("writeOnly", KeywordKind.Annotation),
        Keyword("examples", KeywordKind.Annotation),
    ];

    private static JsonSchemaKeywordDefinition[] CreateContent() =>
    [
        Keyword("contentEncoding", KeywordKind.ContentEncoding),
        Keyword("contentMediaType", KeywordKind.ContentMediaType),
        Keyword("contentSchema", KeywordKind.ContentSchema, JsonSchemaSubschemaLayout.Schema),
    ];

    private sealed class DebugView
    {
        private readonly JsonSchemaVocabulary _vocabulary;

        public DebugView(JsonSchemaVocabulary vocabulary)
        {
            _vocabulary = vocabulary;
        }

        public Uri Id => _vocabulary.Id;

        public IReadOnlyList<JsonSchemaKeywordDefinition> Keywords => _vocabulary.Keywords;
    }
}
