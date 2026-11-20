using System;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Builds a 2020-12 based dialect with a test vocabulary of custom keywords.
/// </summary>
internal static class TestDialect
{
    public static readonly Uri VocabularyId = new("https://example.com/vocab/test");

    public static readonly Uri DialectId = new("https://example.com/dialect/test");

    public static JsonSchemaVocabulary CreateVocabulary(params JsonSchemaKeywordDefinition[] keywords) =>
        JsonSchemaVocabulary.Create(VocabularyId, keywords);

    public static JsonSchemaDialect Create(params JsonSchemaKeywordDefinition[] keywords) =>
        JsonSchemaDialect.Create(DialectId,
        [
            JsonSchemaVocabulary.Draft202012Core,
            JsonSchemaVocabulary.Draft202012Applicator,
            JsonSchemaVocabulary.Draft202012Unevaluated,
            JsonSchemaVocabulary.Draft202012Validation,
            JsonSchemaVocabulary.Draft202012MetaData,
            CreateVocabulary(keywords),
        ]);

    /// <summary>Loads a schema without <c>$schema</c> under a dialect of the given keywords.</summary>
    public static JsonSchemaDocument Load(string json, params JsonSchemaKeywordDefinition[] keywords) =>
        JsonSchemaDocument.Parse(json, new JsonSchemaDocumentOptions { DefaultDialect = Create(keywords) });
}
