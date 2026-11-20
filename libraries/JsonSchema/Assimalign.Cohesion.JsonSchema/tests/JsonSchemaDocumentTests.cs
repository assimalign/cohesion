using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

public class JsonSchemaDocumentTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - JsonSchemaDocument: ";

    [Fact(DisplayName = DisplayPrefix + "Parse: Should refuse text that is not JSON")]
    public void Parse_InvalidJson_ShouldThrowInvalidSchema()
    {
        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse("{\"type\":"));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
        exception.InnerException.ShouldBeAssignableTo<JsonException>();
    }

    [Fact(DisplayName = DisplayPrefix + "Parse: Should refuse UTF-8 with content after the root value")]
    public void Parse_Utf8WithTrailingContent_ShouldThrowInvalidSchema()
    {
        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse("{} []"u8));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
    }

    [Fact(DisplayName = DisplayPrefix + "Parse: Should load UTF-8 followed by whitespace")]
    public void Parse_Utf8_ShouldLoad()
    {
        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"type\":\"string\"} \r\n"u8);

        // Assert
        document.IsValid(TestJson.Parse("\"a\"")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("1")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Parse: Should refuse null text")]
    public void Parse_Null_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => JsonSchemaDocument.Parse((string)null!));
    }

    [Fact(DisplayName = DisplayPrefix + "ParseAsync: Should load a stream and leave it open")]
    public async Task ParseAsync_Stream_ShouldLoadAndLeaveStreamOpen()
    {
        // Arrange
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("{\"minimum\": 3}"));
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));

        // Act
        JsonSchemaDocument document = await JsonSchemaDocument.ParseAsync(stream, cancellationToken: cancellation.Token);

        // Assert
        stream.CanRead.ShouldBeTrue();
        document.IsValid(TestJson.Parse("3")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("2")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "ParseAsync: Should observe a canceled token")]
    public async Task ParseAsync_CanceledToken_ShouldThrowOperationCanceled()
    {
        // Arrange
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("{}"));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => JsonSchemaDocument.ParseAsync(stream, cancellationToken: cancellation.Token));
    }

    [Fact(DisplayName = DisplayPrefix + "ParseAsync: Should refuse a null stream and a stream that is not JSON")]
    public async Task ParseAsync_InvalidInput_ShouldThrow()
    {
        // Arrange
        using MemoryStream stream = new(Encoding.UTF8.GetBytes("{"));

        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(() => JsonSchemaDocument.ParseAsync(null!, cancellationToken: CancellationToken.None));
        JsonSchemaException exception = await Should.ThrowAsync<JsonSchemaException>(() => JsonSchemaDocument.ParseAsync(stream, cancellationToken: CancellationToken.None));
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
    }

    [Theory(DisplayName = DisplayPrefix + "Create: Should refuse a root that is neither an object nor a boolean")]
    [InlineData("1")]
    [InlineData("\"schema\"")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Create_NonSchemaRoot_ShouldThrowInvalidSchema(string json)
    {
        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Create(TestJson.Parse(json)));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
    }

    [Fact(DisplayName = DisplayPrefix + "Create: Should refuse the default element")]
    public void Create_DefaultElement_ShouldThrowInvalidSchema()
    {
        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Create(default));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
    }

    [Fact(DisplayName = DisplayPrefix + "Create: Should evaluate boolean roots")]
    public void Create_BooleanRoot_ShouldAcceptOrRejectEverything()
    {
        // Arrange
        JsonSchemaDocument always = JsonSchemaDocument.Parse("true");
        JsonSchemaDocument never = JsonSchemaDocument.Parse("false");
        JsonElement instance = TestJson.Parse("{\"any\": [1, 2]}");

        // Act
        JsonSchemaEvaluationResult result = never.Evaluate(instance, new JsonSchemaEvaluationOptions { OutputFormat = JsonSchemaOutputFormat.Basic });

        // Assert
        always.IsValid(instance).ShouldBeTrue();
        never.IsValid(instance).ShouldBeFalse();
        result.IsValid.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = DisplayPrefix + "Create: Should copy the value so the source document can be disposed")]
    public void Create_DisposedSource_ShouldKeepWorking()
    {
        // Arrange
        JsonSchemaDocument document;
        using (JsonDocument source = JsonDocument.Parse("{\"properties\":{\"a\":{\"type\":\"integer\"}}}"))
        {
            document = JsonSchemaDocument.Create(source.RootElement);
        }

        // Act
        bool valid = document.IsValid(TestJson.Parse("{\"a\": 1}"));
        bool invalid = document.IsValid(TestJson.Parse("{\"a\": \"1\"}"));

        // Assert
        valid.ShouldBeTrue();
        invalid.ShouldBeFalse();
        document.Source.GetProperty("properties").GetProperty("a").GetProperty("type").GetString().ShouldBe("integer");
    }

    [Theory(DisplayName = DisplayPrefix + "Create: Should refuse a $schema that names no known dialect")]
    [InlineData("{\"$schema\": \"https://example.com/unknown-dialect\"}")]
    [InlineData("{\"$schema\": \"relative/dialect\"}")]
    public void Create_UnknownDialect_ShouldThrowUnknownDialect(string json)
    {
        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse(json));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.UnknownDialect);
    }

    [Theory(DisplayName = DisplayPrefix + "Create: Should refuse a known keyword with a value of the wrong shape, naming it and its schema")]
    [InlineData("{\"minLength\": -1}", "", "minLength")]
    [InlineData("{\"type\": 5}", "", "type")]
    [InlineData("{\"required\": \"a\"}", "", "required")]
    [InlineData("{\"properties\": []}", "", "properties")]
    [InlineData("{\"$ref\": 5}", "", "$ref")]
    [InlineData("{\"$defs\": {\"a\": {\"maxItems\": 1.5}}}", "/$defs/a", "maxItems")]
    public void Create_InvalidKeywordValue_ShouldThrowInvalidSchema(string json, string schemaLocation, string keyword)
    {
        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse(json));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
        exception.Location.ShouldBe("#" + schemaLocation);
        exception.Message.ShouldContain("'" + keyword + "'", Case.Sensitive);
    }

    [Theory(DisplayName = DisplayPrefix + "Create: Should refuse an invalid URI reference or an identifier with a fragment")]
    [InlineData("{\"$ref\": \"1http://example.com\"}")]
    [InlineData("{\"$id\": \"https://example.com/schema#fragment\"}")]
    public void Create_InvalidUri_ShouldThrowInvalidUri(string json)
    {
        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse(json));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidUri);
    }

    [Theory(DisplayName = DisplayPrefix + "Create: Should refuse an anchor that is empty or not a string")]
    [InlineData("{\"$anchor\": \"\"}")]
    [InlineData("{\"$anchor\": 5}")]
    [InlineData("{\"$dynamicAnchor\": false}")]
    public void Create_InvalidAnchor_ShouldThrowInvalidSchema(string json)
    {
        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse(json));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
    }

    [Fact(DisplayName = DisplayPrefix + "Create: Should validate against the meta-schema only when asked")]
    public void Create_ValidateSchema_ShouldCheckMetaSchema()
    {
        // Arrange
        const string json = "{\"title\": 5}";

        // Act
        JsonSchemaDocument lenient = JsonSchemaDocument.Parse(json);
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse(json, new JsonSchemaDocumentOptions { ValidateSchema = true }));

        // Assert
        lenient.IsValid(TestJson.Parse("1")).ShouldBeTrue();
        exception.Code.ShouldBe(JsonSchemaErrorCode.MetaSchemaViolation);
    }

    [Theory(DisplayName = DisplayPrefix + "Create: Should load a conforming schema with meta-schema validation in every dialect")]
    [InlineData("http://json-schema.org/draft-04/schema#")]
    [InlineData("http://json-schema.org/draft-06/schema#")]
    [InlineData("http://json-schema.org/draft-07/schema#")]
    [InlineData("https://json-schema.org/draft/2019-09/schema")]
    [InlineData("https://json-schema.org/draft/2020-12/schema")]
    public void Create_ValidateSchemaConforming_ShouldLoad(string dialect)
    {
        // Arrange
        string json = "{\"$schema\": \"" + dialect + "\", \"type\": \"object\", \"properties\": {\"a\": {\"type\": \"string\"}}}";

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Parse(json, new JsonSchemaDocumentOptions { ValidateSchema = true });

        // Assert
        document.Dialect.Id.OriginalString.ShouldBe(dialect);
        document.IsValid(TestJson.Parse("{\"a\": \"b\"}")).ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Create: Should apply the default dialect to a schema without $schema")]
    public void Create_DefaultDialect_ShouldApplyToSchemaWithoutSchemaKeyword()
    {
        // Arrange
        const string json = "{\"items\": [{\"type\": \"string\"}]}";
        JsonSchemaDocumentOptions options = new() { DefaultDialect = JsonSchemaDialect.Draft07 };

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Parse(json, options);

        // Assert
        document.Dialect.ShouldBeSameAs(JsonSchemaDialect.Draft07);
        document.IsValid(TestJson.Parse("[\"a\", 1]")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("[1]")).ShouldBeFalse();
        Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse(json)).Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
    }

    [Fact(DisplayName = DisplayPrefix + "Create: Should let $schema override the default dialect")]
    public void Create_SchemaKeyword_ShouldOverrideDefaultDialect()
    {
        // Arrange
        JsonSchemaDocumentOptions options = new() { DefaultDialect = JsonSchemaDialect.Draft07 };

        // Act
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"$schema\": \"http://json-schema.org/draft-04/schema#\", \"maximum\": 5, \"exclusiveMaximum\": true}", options);

        // Assert
        document.Dialect.ShouldBeSameAs(JsonSchemaDialect.Draft04);
        document.IsValid(TestJson.Parse("4")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("5")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Options: Should refuse a relative base URI and a non-positive regex timeout")]
    public void Options_InvalidValues_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => new JsonSchemaDocumentOptions { BaseUri = new Uri("schemas/a.json", UriKind.Relative) });
        Should.Throw<ArgumentOutOfRangeException>(() => new JsonSchemaDocumentOptions { RegexMatchTimeout = TimeSpan.Zero });
        Should.Throw<ArgumentOutOfRangeException>(() => new JsonSchemaEvaluationOptions { MaxEvaluationDepth = 0 });
    }

    [Fact(DisplayName = DisplayPrefix + "BaseUri: Should report the retrieval URI, or the root $id resolved against it")]
    public void BaseUri_RetrievalUriAndId_ShouldResolve()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        Uri retrieval = new("https://example.com/schemas/root.json");

        // Act
        JsonSchemaDocument plain = JsonSchemaDocument.Parse("{}", new JsonSchemaDocumentOptions { BaseUri = retrieval, Registry = registry });
        JsonSchemaDocument identified = JsonSchemaDocument.Parse("{\"$id\": \"other.json\"}", new JsonSchemaDocumentOptions { BaseUri = new Uri("https://example.com/schemas/loaded.json"), Registry = registry });
        JsonSchemaDocument anonymous = JsonSchemaDocument.Parse("{}");

        // Assert
        plain.BaseUri.ShouldBe(retrieval);
        identified.BaseUri.ShouldBe(new Uri("https://example.com/schemas/other.json"));
        registry.TryGetSchema(new Uri("https://example.com/schemas/loaded.json"), out IJsonSchema? byRetrieval).ShouldBeTrue();
        byRetrieval!.Document.ShouldBeSameAs(identified);
        anonymous.BaseUri.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "Create: Should refuse two embedded resources with the same identifier")]
    public void Create_DuplicateEmbeddedId_ShouldThrowDuplicateResource()
    {
        // Arrange
        const string json = "{\"$defs\": {\"a\": {\"$id\": \"https://example.com/x\"}, \"b\": {\"$id\": \"https://example.com/x\"}}}";

        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse(json));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.DuplicateResource);
    }

    [Fact(DisplayName = DisplayPrefix + "Create: Should refuse a document whose identifier is already registered and keep the first")]
    public void Create_IdAlreadyRegistered_ShouldThrowDuplicateResource()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        JsonSchemaDocumentOptions options = new() { Registry = registry };
        JsonSchemaDocument first = JsonSchemaDocument.Parse("{\"$id\": \"https://example.com/shared\", \"type\": \"string\"}", options);

        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse("{\"$id\": \"https://example.com/shared\", \"type\": \"number\"}", options));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.DuplicateResource);
        registry.TryGetSchema(new Uri("https://example.com/shared"), out IJsonSchema? schema).ShouldBeTrue();
        schema!.Document.ShouldBeSameAs(first);
    }

    [Fact(DisplayName = DisplayPrefix + "TryGetSchema: Should find a subschema and evaluate with it")]
    public void TryGetSchema_Subschema_ShouldEvaluate()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"$defs\": {\"name\": {\"type\": \"string\", \"minLength\": 2}}}");

        // Act
        bool found = document.TryGetSchema(new JsonPointer("/$defs/name"), out IJsonSchema? schema);

        // Assert
        found.ShouldBeTrue();
        schema!.Location.ShouldBe(new JsonPointer("/$defs/name"));
        schema.Document.ShouldBeSameAs(document);
        schema.Dialect.ShouldBeSameAs(document.Dialect);
        schema.IsValid(TestJson.Parse("\"ab\"")).ShouldBeTrue();
        schema.IsValid(TestJson.Parse("\"a\"")).ShouldBeFalse();
        document.TryGetSchema(JsonPointer.Root, out IJsonSchema? root).ShouldBeTrue();
        root.ShouldNotBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "TryGetSchema: Should compile a schema inside an unknown keyword on demand")]
    public void TryGetSchema_InsideUnknownKeyword_ShouldCompile()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"x-definitions\": {\"positive\": {\"exclusiveMinimum\": 0}}}");

        // Act
        bool found = document.TryGetSchema(new JsonPointer("/x-definitions/positive"), out IJsonSchema? schema);

        // Assert
        found.ShouldBeTrue();
        schema!.IsValid(TestJson.Parse("1")).ShouldBeTrue();
        schema.IsValid(TestJson.Parse("0")).ShouldBeFalse();
    }

    [Theory(DisplayName = DisplayPrefix + "TryGetSchema: Should not find a location that holds no schema")]
    [InlineData("/$defs/missing")]
    [InlineData("/minLength")]
    [InlineData("/required/0")]
    public void TryGetSchema_NotASchema_ShouldReturnFalse(string location)
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"minLength\": 1, \"required\": [\"a\"], \"$defs\": {}}");

        // Act
        bool found = document.TryGetSchema(new JsonPointer(location), out IJsonSchema? schema);

        // Assert
        found.ShouldBeFalse();
        schema.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "TryGetSchema: Should refuse the default pointer")]
    public void TryGetSchema_DefaultPointer_ShouldThrowArgumentException()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{}");

        // Act & Assert
        Should.Throw<ArgumentException>(() => document.TryGetSchema(default, out _));
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should refuse the default instance")]
    public void Evaluate_DefaultInstance_ShouldThrowArgumentException()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"type\": \"string\"}");

        // Act & Assert
        Should.Throw<ArgumentException>(() => document.Evaluate(default));
        Should.Throw<ArgumentException>(() => document.IsValid(default));
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should refuse a reference that recurses without consuming the instance")]
    public void Evaluate_InfiniteRecursion_ShouldThrowEvaluationDepthExceeded()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"$defs\": {\"a\": {\"$ref\": \"#/$defs/b\"}, \"b\": {\"$ref\": \"#/$defs/a\"}}, \"$ref\": \"#/$defs/a\"}");

        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => document.IsValid(TestJson.Parse("1")));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.EvaluationDepthExceeded);
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should stop at the configured depth")]
    public void Evaluate_DeepInstance_ShouldThrowAtMaxDepth()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"items\": {\"$ref\": \"#\"}}");
        JsonElement instance = TestJson.Parse(new string('[', 40) + new string(']', 40));
        JsonSchemaEvaluationOptions shallow = new() { MaxEvaluationDepth = 16 };

        // Act
        bool valid = document.IsValid(instance);
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => document.IsValid(instance, shallow));

        // Assert
        valid.ShouldBeTrue();
        exception.Code.ShouldBe(JsonSchemaErrorCode.EvaluationDepthExceeded);
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should give the same answers from many threads")]
    public void Evaluate_Concurrently_ShouldBeConsistent()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("""
            {
              "type": "object",
              "properties": { "tags": { "type": "array", "items": { "type": "string" }, "uniqueItems": true } },
              "unevaluatedProperties": false
            }
            """);
        JsonElement valid = TestJson.Parse("{\"tags\": [\"a\", \"b\"]}");
        JsonElement invalid = TestJson.Parse("{\"tags\": [\"a\", \"a\"], \"extra\": 1}");
        int mismatches = 0;

        // Act
        Parallel.For(0, 2000, index =>
        {
            bool expected = index % 2 == 0;
            JsonSchemaEvaluationOptions options = new() { OutputFormat = (JsonSchemaOutputFormat)(index % 4) };
            if (document.Evaluate(expected ? valid : invalid, options).IsValid != expected)
            {
                Interlocked.Increment(ref mismatches);
            }
        });

        // Assert
        mismatches.ShouldBe(0);
    }
}
