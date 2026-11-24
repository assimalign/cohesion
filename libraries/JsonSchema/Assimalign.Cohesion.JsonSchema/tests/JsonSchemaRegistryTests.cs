using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

public class JsonSchemaRegistryTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - JsonSchemaRegistry: ";

    [Fact(DisplayName = DisplayPrefix + "Register: Should let another document reference a registered one")]
    public void Register_Document_ShouldResolveCrossDocumentReference()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        registry.Register(new Uri("https://example.com/address.json"), TestJson.Parse("{\"type\": \"object\", \"required\": [\"city\"]}"));
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"properties\": {\"home\": {\"$ref\": \"https://example.com/address.json\"}}}", new JsonSchemaDocumentOptions { Registry = registry });

        // Act
        bool valid = document.IsValid(TestJson.Parse("{\"home\": {\"city\": \"Paris\"}}"));
        bool invalid = document.IsValid(TestJson.Parse("{\"home\": {}}"));

        // Assert
        valid.ShouldBeTrue();
        invalid.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Register: Should refuse a null or relative URI")]
    public void Register_InvalidUri_ShouldThrow()
    {
        // Arrange
        JsonSchemaRegistry registry = new();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => registry.Register(null!, TestJson.Parse("{}")));
        Should.Throw<ArgumentException>(() => registry.Register(new Uri("a.json", UriKind.Relative), TestJson.Parse("{}")));
        Should.Throw<ArgumentNullException>(() => registry.Register((JsonSchemaDialect)null!));
        Should.Throw<ArgumentNullException>(() => registry.Register((JsonSchemaVocabulary)null!));
    }

    [Fact(DisplayName = DisplayPrefix + "Register: Should use the default dialect for a document without $schema")]
    public void Register_DefaultDialect_ShouldApply()
    {
        // Arrange
        JsonSchemaRegistry registry = new();

        // Act
        JsonSchemaDocument document = registry.Register(new Uri("https://example.com/tuple.json"), TestJson.Parse("{\"items\": [{\"type\": \"string\"}]}"), JsonSchemaDialect.Draft201909);

        // Assert
        document.Dialect.ShouldBeSameAs(JsonSchemaDialect.Draft201909);
        document.BaseUri.ShouldBe(new Uri("https://example.com/tuple.json"));
    }

    [Fact(DisplayName = DisplayPrefix + "Resolver: Should be asked once for a document it supplies")]
    public void Resolver_SuppliedDocument_ShouldBeCalledOnce()
    {
        // Arrange
        CountingResolver resolver = new CountingResolver().Add("https://example.com/remote.json", "{\"$defs\": {\"even\": {\"multipleOf\": 2}}}");
        JsonSchemaRegistry registry = new() { Resolver = resolver };
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"$ref\": \"https://example.com/remote.json#/$defs/even\"}", new JsonSchemaDocumentOptions { Registry = registry });

        // Act
        bool first = document.IsValid(TestJson.Parse("4"));
        bool second = document.IsValid(TestJson.Parse("3"));
        bool third = document.IsValid(TestJson.Parse("6"));

        // Assert
        first.ShouldBeTrue();
        second.ShouldBeFalse();
        third.ShouldBeTrue();
        resolver.Calls.ShouldBe(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should fail an unresolvable reference, then resolve it once the target is registered")]
    public void Evaluate_ReferenceRegisteredLater_ShouldResolveOnNextEvaluation()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"$ref\": \"https://example.com/later.json\"}", new JsonSchemaDocumentOptions { Registry = registry });

        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => document.IsValid(TestJson.Parse("1")));
        registry.Register(new Uri("https://example.com/later.json"), TestJson.Parse("{\"type\": \"integer\"}"));
        bool valid = document.IsValid(TestJson.Parse("1"));
        bool invalid = document.IsValid(TestJson.Parse("1.5"));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.UnresolvedReference);
        valid.ShouldBeTrue();
        invalid.ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "TryGetSchema: Should find resources by JSON Pointer and anchor fragments")]
    public void TryGetSchema_Fragments_ShouldResolve()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        registry.Register(new Uri("https://example.com/s.json"), TestJson.Parse("{\"$defs\": {\"a\": {\"$anchor\": \"named\", \"type\": \"string\"}}}"));

        // Act
        bool byPointer = registry.TryGetSchema(new Uri("https://example.com/s.json#/$defs/a"), out IJsonSchema? pointed);
        bool byAnchor = registry.TryGetSchema(new Uri("https://example.com/s.json#named"), out IJsonSchema? anchored);
        bool missingAnchor = registry.TryGetSchema(new Uri("https://example.com/s.json#missing"), out _);
        bool missingDocument = registry.TryGetSchema(new Uri("https://example.com/none.json"), out _);

        // Assert
        byPointer.ShouldBeTrue();
        byAnchor.ShouldBeTrue();
        anchored.ShouldBeSameAs(pointed);
        pointed!.Location.ShouldBe(new JsonPointer("/$defs/a"));
        missingAnchor.ShouldBeFalse();
        missingDocument.ShouldBeFalse();
    }

    [Theory(DisplayName = DisplayPrefix + "TryGetSchema: Should provide every built-in meta-schema without a resolver")]
    [InlineData("http://json-schema.org/draft-04/schema#")]
    [InlineData("http://json-schema.org/draft-06/schema#")]
    [InlineData("http://json-schema.org/draft-07/schema#")]
    [InlineData("https://json-schema.org/draft/2019-09/schema")]
    [InlineData("https://json-schema.org/draft/2020-12/schema")]
    [InlineData("https://json-schema.org/draft/2020-12/meta/validation")]
    public void TryGetSchema_MetaSchema_ShouldBeAvailable(string uri)
    {
        // Arrange
        JsonSchemaRegistry registry = new();

        // Act
        bool found = registry.TryGetSchema(new Uri(uri), out IJsonSchema? schema);

        // Assert
        found.ShouldBeTrue();
        schema!.IsValid(TestJson.Parse("{\"type\": \"string\"}")).ShouldBeTrue();
        schema.IsValid(TestJson.Parse("{\"type\": 5}")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "TryGetSchema: Should not find a relative URI and refuse null")]
    public void TryGetSchema_RelativeOrNull_ShouldReturnFalseOrThrow()
    {
        // Arrange
        JsonSchemaRegistry registry = new();

        // Act
        bool found = registry.TryGetSchema(new Uri("schema.json", UriKind.Relative), out IJsonSchema? schema);

        // Assert
        found.ShouldBeFalse();
        schema.ShouldBeNull();
        Should.Throw<ArgumentNullException>(() => registry.TryGetSchema(null!, out _));
    }

    [Fact(DisplayName = DisplayPrefix + "TryGetSchema: Should report a resolved document that cannot load")]
    public void TryGetSchema_ResolvedDocumentInvalid_ShouldThrow()
    {
        // Arrange
        JsonSchemaRegistry registry = new() { Resolver = new CountingResolver().Add("https://example.com/broken.json", "[1, 2]") };

        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => registry.TryGetSchema(new Uri("https://example.com/broken.json"), out _));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
    }

    [Fact(DisplayName = DisplayPrefix + "TryGetSchema: Should load a resolved document once when many threads ask for it")]
    public void TryGetSchema_Concurrently_ShouldAllFindTheSameDocument()
    {
        // Arrange
        CountingResolver resolver = new CountingResolver().Add("https://example.com/shared.json", "{\"type\": \"boolean\"}");
        JsonSchemaRegistry registry = new() { Resolver = resolver };
        IJsonSchema?[] found = new IJsonSchema?[64];

        // Act
        Parallel.For(0, found.Length, index =>
        {
            registry.TryGetSchema(new Uri("https://example.com/shared.json"), out found[index]);
        });

        // Assert
        foreach (IJsonSchema? schema in found)
        {
            schema.ShouldNotBeNull();
            schema.Document.ShouldBeSameAs(found[0]!.Document);
        }

        resolver.Calls.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact(DisplayName = DisplayPrefix + "Register: Should accept the same dialect twice and refuse a different one under its identifier")]
    public void Register_Dialect_ShouldRefuseDifferentDuplicate()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        JsonSchemaDialect dialect = TestDialect.Create();
        JsonSchemaDialect other = TestDialect.Create();

        // Act
        registry.Register(dialect);
        registry.Register(dialect);
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => registry.Register(other));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.DuplicateResource);
    }

    [Fact(DisplayName = DisplayPrefix + "Register: Should accept the same vocabulary twice and refuse a different one under its identifier")]
    public void Register_Vocabulary_ShouldRefuseDifferentDuplicate()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        JsonSchemaVocabulary vocabulary = TestDialect.CreateVocabulary();
        JsonSchemaVocabulary other = TestDialect.CreateVocabulary();

        // Act
        registry.Register(vocabulary);
        registry.Register(vocabulary);
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => registry.Register(other));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.DuplicateResource);
    }

    [Fact(DisplayName = DisplayPrefix + "Register: Should make a registered dialect available to $schema")]
    public void Register_Dialect_ShouldBeSelectedBySchemaKeyword()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        registry.Register(TestDialect.Create(JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword())));

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"$schema\": \"https://example.com/dialect/test\", \"x-even\": true}", new JsonSchemaDocumentOptions { Registry = registry });

        // Assert
        document.Dialect.Id.ShouldBe(TestDialect.DialectId);
        document.IsValid(TestJson.Parse("2")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("3")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Register: Should make a registered vocabulary available to a meta-schema's $vocabulary")]
    public void Register_Vocabulary_ShouldBeSelectedByMetaSchema()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        registry.Register(TestDialect.CreateVocabulary(JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword())));
        registry.Register(new Uri("https://example.com/meta/even"), TestJson.Parse("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$vocabulary": {
                "https://json-schema.org/draft/2020-12/vocab/core": true,
                "https://json-schema.org/draft/2020-12/vocab/applicator": true,
                "https://example.com/vocab/test": true,
                "https://example.com/vocab/optional-unknown": false
              },
              "$dynamicAnchor": "meta"
            }
            """));

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"$schema\": \"https://example.com/meta/even\", \"x-even\": true, \"minimum\": 10}", new JsonSchemaDocumentOptions { Registry = registry });

        // Assert
        document.IsValid(TestJson.Parse("3")).ShouldBeFalse();
        document.IsValid(TestJson.Parse("2")).ShouldBeTrue();
        document.Dialect.TryGetKeyword("minimum", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Create: Should refuse a meta-schema that requires an unknown vocabulary")]
    public void Create_RequiredUnknownVocabulary_ShouldThrowUnsupportedVocabulary()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        registry.Register(new Uri("https://example.com/meta/unknown"), TestJson.Parse("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$vocabulary": {
                "https://json-schema.org/draft/2020-12/vocab/core": true,
                "https://example.com/vocab/required-unknown": true
              }
            }
            """));

        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse("{\"$schema\": \"https://example.com/meta/unknown\"}", new JsonSchemaDocumentOptions { Registry = registry }));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.UnsupportedVocabulary);
    }
}
