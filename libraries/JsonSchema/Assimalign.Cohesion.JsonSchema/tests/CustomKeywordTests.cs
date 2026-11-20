using System;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

public class CustomKeywordTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Custom keywords: ";

    private static readonly JsonSchemaEvaluationOptions _basic = new() { OutputFormat = JsonSchemaOutputFormat.Basic };

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should fail with the keyword's message at its location")]
    public void Evaluate_KeywordFails_ShouldReportMessage()
    {
        // Arrange
        JsonSchemaDocument document = TestDialect.Load("{\"properties\": {\"n\": {\"x-even\": true}}}", JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword()));

        // Act
        JsonSchemaEvaluationResult result = document.Evaluate(TestJson.Parse("{\"n\": 3}"), _basic);

        // Assert
        document.IsValid(TestJson.Parse("{\"n\": 4}")).ShouldBeTrue();
        result.IsValid.ShouldBeFalse();
        JsonSchemaEvaluationResult error = result.Errors.Single(unit => unit.Keyword == "x-even");
        error.Error.ShouldBe(EvenKeyword.Message);
        error.KeywordLocation.Value.ShouldBe("/properties/n/x-even");
        error.InstanceLocation.Value.ShouldBe("/n");
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should report a generic message that names the keyword when none is given")]
    public void Evaluate_KeywordFailsWithoutMessage_ShouldReportGenericMessage()
    {
        // Arrange
        JsonSchemaDocument document = TestDialect.Load("{\"x-even\": true}", JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword(genericMessage: true)));

        // Act
        JsonSchemaEvaluationResult result = document.Evaluate(TestJson.Parse("5"), _basic);

        // Assert
        result.Errors.ShouldHaveSingleItem().Error.ShouldNotBeNull().ShouldContain("x-even", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should report the keyword's annotation when the schema is valid")]
    public void Evaluate_KeywordAnnotates_ShouldReportAnnotation()
    {
        // Arrange
        JsonSchemaDocument document = TestDialect.Load("{\"x-label\": {\"text\": \"Amount\"}, \"minimum\": 0}", JsonSchemaKeywordDefinition.Create("x-label", _ => new AnnotateValueKeyword()));

        // Act
        JsonSchemaEvaluationResult valid = document.Evaluate(TestJson.Parse("1"), _basic);
        JsonSchemaEvaluationResult invalid = document.Evaluate(TestJson.Parse("-1"), new JsonSchemaEvaluationOptions { OutputFormat = JsonSchemaOutputFormat.Verbose });

        // Assert
        valid.Annotations.Single(unit => unit.Keyword == "x-label").Annotation!.Value.GetRawText().ShouldBe("{\"text\": \"Amount\"}");
        invalid.Annotations.Concat(invalid.Errors).Single(unit => unit.Keyword == "x-label").Annotation.ShouldBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should apply a subschema in place and count what it evaluated")]
    public void Evaluate_InPlaceSubschema_ShouldCountEvaluatedProperties()
    {
        // Arrange
        JsonSchemaDocument document = TestDialect.Load(
            "{\"x-apply\": {\"properties\": {\"a\": {\"type\": \"string\"}}}, \"unevaluatedProperties\": false}",
            JsonSchemaKeywordDefinition.Create("x-apply", _ => new ApplyInPlaceKeyword(), JsonSchemaSubschemaLayout.Schema));

        // Act & Assert
        document.IsValid(TestJson.Parse("{\"a\": \"x\"}")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("{\"a\": 1}")).ShouldBeFalse();
        document.IsValid(TestJson.Parse("{\"b\": \"x\"}")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should apply member subschemas and honor marked properties")]
    public void Evaluate_MemberSubschemas_ShouldMarkProperties()
    {
        // Arrange
        JsonSchemaDocument document = TestDialect.Load(
            "{\"x-cover\": {\"a\": {\"type\": \"string\"}, \"b/c\": {\"type\": \"integer\"}}, \"unevaluatedProperties\": false}",
            JsonSchemaKeywordDefinition.Create("x-cover", _ => new CoverPropertiesKeyword(), JsonSchemaSubschemaLayout.SchemaMap));

        // Act
        JsonSchemaEvaluationResult result = document.Evaluate(TestJson.Parse("{\"b/c\": \"x\"}"), _basic);

        // Assert
        document.IsValid(TestJson.Parse("{\"a\": \"x\", \"b/c\": 1}")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("{\"a\": \"x\", \"d\": 1}")).ShouldBeFalse();
        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(unit => unit.KeywordLocation.Value == "/x-cover/b~1c/type" && unit.InstanceLocation.Value == "/b~1c");
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should apply item subschemas and honor marked items")]
    public void Evaluate_ItemSubschemas_ShouldMarkItems()
    {
        // Arrange
        JsonSchemaDocument document = TestDialect.Load(
            "{\"x-items\": [{\"type\": \"string\"}, {\"type\": \"boolean\"}], \"unevaluatedItems\": false}",
            JsonSchemaKeywordDefinition.Create("x-items", _ => new CoverItemsKeyword(), JsonSchemaSubschemaLayout.SchemaArray));

        // Act & Assert
        document.IsValid(TestJson.Parse("[\"a\", true]")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("[\"a\"]")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("[\"a\", true, 3]")).ShouldBeFalse();
        document.IsValid(TestJson.Parse("[1]")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should compile subschemas of either shape for a schema-or-array layout")]
    public void Evaluate_SchemaOrSchemaArray_ShouldCompileBothShapes()
    {
        // Arrange
        JsonSchemaKeywordDefinition definition = JsonSchemaKeywordDefinition.Create("x-either", _ => new ApplyInPlaceKeyword(), JsonSchemaSubschemaLayout.SchemaOrSchemaArray);
        JsonSchemaDocument single = TestDialect.Load("{\"x-either\": {\"type\": \"string\"}}", definition);
        JsonSchemaDocument array = TestDialect.Load("{\"x-either\": [{\"type\": \"string\"}]}", definition);

        // Act & Assert
        single.IsValid(TestJson.Parse("\"a\"")).ShouldBeTrue();
        single.IsValid(TestJson.Parse("1")).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => array.IsValid(TestJson.Parse("\"a\"")));
        array.TryGetSchema(new JsonPointer("/x-either/0"), out IJsonSchema? item).ShouldBeTrue();
        item!.IsValid(TestJson.Parse("1")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should report the instance location to the keyword")]
    public void Evaluate_InstanceLocation_ShouldBeReported()
    {
        // Arrange
        JsonSchemaDocument document = TestDialect.Load(
            "{\"properties\": {\"list\": {\"items\": {\"x-where\": true}}}}",
            JsonSchemaKeywordDefinition.Create("x-where", _ => new InstanceLocationKeyword()));

        // Act
        JsonSchemaEvaluationResult flag = document.Evaluate(TestJson.Parse("{\"list\": [0, 1]}"));
        JsonSchemaEvaluationResult basic = document.Evaluate(TestJson.Parse("{\"list\": [0, 1]}"), _basic);

        // Assert
        flag.IsValid.ShouldBeTrue();
        basic.Annotations.Where(unit => unit.Keyword == "x-where").Select(unit => unit.Annotation!.Value.GetString()).ShouldBe(["/list/0", "/list/1"]);
    }

    [Fact(DisplayName = DisplayPrefix + "Context: Should refuse use after the keyword returned")]
    public void Context_AfterReturn_ShouldThrowInvalidOperation()
    {
        // Arrange
        CapturingKeyword keyword = new();
        JsonSchemaDocument document = TestDialect.Load("{\"x-capture\": true}", JsonSchemaKeywordDefinition.Create("x-capture", _ => keyword));
        document.IsValid(TestJson.Parse("{\"a\": [1]}")).ShouldBeTrue();
        IJsonSchemaKeywordContext context = keyword.Captured.ShouldNotBeNull();

        // Act & Assert
        context.Keyword.ShouldBe("x-capture");
        Should.Throw<InvalidOperationException>(() => context.Fail());
        Should.Throw<InvalidOperationException>(() => context.Annotate(TestJson.Parse("1")));
        Should.Throw<InvalidOperationException>(() => _ = context.InstanceLocation);
        Should.Throw<InvalidOperationException>(() => context.MarkPropertyEvaluated("a"));
        Should.Throw<InvalidOperationException>(() => context.MarkItemEvaluated(0));
        Should.Throw<InvalidOperationException>(() => context.EvaluateSubschema(JsonPointer.Root));
    }

    [Fact(DisplayName = DisplayPrefix + "Context: Should refuse an undefined annotation, a missing subschema and a missing member or item")]
    public void Context_InvalidArguments_ShouldThrow()
    {
        // Arrange
        JsonSchemaDocument undefined = TestDialect.Load("{\"x-undefined\": true}", JsonSchemaKeywordDefinition.Create("x-undefined", _ => new UndefinedAnnotationKeyword()));
        JsonSchemaDocument noSubschema = TestDialect.Load("{\"x-apply\": {}}", JsonSchemaKeywordDefinition.Create("x-apply", _ => new ApplyInPlaceKeyword()));
        JsonSchemaDocument cover = TestDialect.Load("{\"x-cover\": {\"a\": true}}", JsonSchemaKeywordDefinition.Create("x-cover", _ => new CoverPropertiesKeyword(), JsonSchemaSubschemaLayout.SchemaMap));
        JsonSchemaDocument items = TestDialect.Load("{\"x-items\": [true]}", JsonSchemaKeywordDefinition.Create("x-items", _ => new CoverItemsKeyword(), JsonSchemaSubschemaLayout.SchemaArray));

        // Act & Assert
        Should.Throw<ArgumentException>(() => undefined.Evaluate(TestJson.Parse("1"), _basic));
        Should.Throw<ArgumentException>(() => noSubschema.IsValid(TestJson.Parse("1")));
        cover.IsValid(TestJson.Parse("{\"b\": 1}")).ShouldBeTrue();
        items.IsValid(TestJson.Parse("[]")).ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Load: Should call the factory once per occurrence, with the keyword's value")]
    public void Load_Factory_ShouldBeCalledOncePerOccurrence()
    {
        // Arrange
        int calls = 0;
        string?[] values = new string?[2];
        JsonSchemaKeywordDefinition definition = JsonSchemaKeywordDefinition.Create("x-even", value =>
        {
            values[Interlocked.Increment(ref calls) - 1] = value.GetRawText();
            return new EvenKeyword();
        });
        JsonSchemaDocument document = TestDialect.Load("{\"x-even\": true, \"properties\": {\"a\": {\"x-even\": false}}}", definition);

        // Act
        for (int i = 0; i < 10; i++)
        {
            document.IsValid(TestJson.Parse("{\"a\": 3}"));
        }

        // Assert
        calls.ShouldBe(2);
        values.ShouldBe(["true", "false"], ignoreOrder: true);
    }

    [Fact(DisplayName = DisplayPrefix + "Load: Should fail when the factory throws or returns null")]
    public void Load_FactoryFails_ShouldFailLoad()
    {
        // Arrange
        JsonSchemaKeywordDefinition throwing = JsonSchemaKeywordDefinition.Create("x-bad", _ => throw new FormatException("bad value"));
        JsonSchemaKeywordDefinition returningNull = JsonSchemaKeywordDefinition.Create("x-null", _ => null!);

        // Act & Assert
        Should.Throw<FormatException>(() => TestDialect.Load("{\"x-bad\": 1}", throwing)).Message.ShouldBe("bad value");
        Should.Throw<InvalidOperationException>(() => TestDialect.Load("{\"x-null\": 1}", returningNull)).Message.ShouldContain("x-null", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Load: Should refuse a subschema of the wrong shape for the layout")]
    public void Load_LayoutMismatch_ShouldThrowInvalidSchema()
    {
        // Arrange
        JsonSchemaKeywordDefinition definition = JsonSchemaKeywordDefinition.Create("x-items", _ => new CoverItemsKeyword(), JsonSchemaSubschemaLayout.SchemaArray);

        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => TestDialect.Load("{\"x-items\": {\"type\": \"string\"}}", definition));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidSchema);
    }

    [Fact(DisplayName = DisplayPrefix + "Definition: Should validate its arguments")]
    public void Definition_InvalidArguments_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => JsonSchemaKeywordDefinition.Create(null!, _ => new EvenKeyword()));
        Should.Throw<ArgumentException>(() => JsonSchemaKeywordDefinition.Create(string.Empty, _ => new EvenKeyword()));
        Should.Throw<ArgumentNullException>(() => JsonSchemaKeywordDefinition.Create("x-even", null!));
        Should.Throw<ArgumentOutOfRangeException>(() => JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword(), (JsonSchemaSubschemaLayout)99));
        JsonSchemaKeywordDefinition definition = JsonSchemaKeywordDefinition.Create("x-map", _ => new EvenKeyword(), JsonSchemaSubschemaLayout.SchemaMap);
        definition.Name.ShouldBe("x-map");
        definition.SubschemaLayout.ShouldBe(JsonSchemaSubschemaLayout.SchemaMap);
    }

    [Fact(DisplayName = DisplayPrefix + "Vocabulary: Should validate its arguments")]
    public void Vocabulary_InvalidArguments_ShouldThrow()
    {
        // Arrange
        JsonSchemaKeywordDefinition even = JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword());

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => JsonSchemaVocabulary.Create(null!, [even]));
        Should.Throw<ArgumentNullException>(() => JsonSchemaVocabulary.Create(TestDialect.VocabularyId, null!));
        Should.Throw<ArgumentNullException>(() => JsonSchemaVocabulary.Create(TestDialect.VocabularyId, [even, null!]));
        Should.Throw<ArgumentException>(() => JsonSchemaVocabulary.Create(new Uri("vocab", UriKind.Relative), [even]));
        Should.Throw<ArgumentException>(() => JsonSchemaVocabulary.Create(TestDialect.VocabularyId, [even, JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword())]));
        JsonSchemaVocabulary vocabulary = JsonSchemaVocabulary.Create(TestDialect.VocabularyId, [even]);
        vocabulary.Id.ShouldBe(TestDialect.VocabularyId);
        vocabulary.Keywords.ShouldHaveSingleItem().ShouldBeSameAs(even);
    }

    [Fact(DisplayName = DisplayPrefix + "Dialect: Should require exactly one core vocabulary and distinct keywords")]
    public void Dialect_InvalidVocabularies_ShouldThrow()
    {
        // Arrange
        JsonSchemaVocabulary custom = TestDialect.CreateVocabulary(JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword()));
        JsonSchemaVocabulary redefinesType = JsonSchemaVocabulary.Create(new Uri("https://example.com/vocab/type"), [JsonSchemaKeywordDefinition.Create("type", _ => new EvenKeyword())]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => JsonSchemaDialect.Create(TestDialect.DialectId, [custom]));
        Should.Throw<ArgumentException>(() => JsonSchemaDialect.Create(TestDialect.DialectId, [JsonSchemaVocabulary.Draft202012Core, JsonSchemaVocabulary.Draft201909Core]));
        Should.Throw<ArgumentException>(() => JsonSchemaDialect.Create(TestDialect.DialectId, [JsonSchemaVocabulary.Draft202012Core, JsonSchemaVocabulary.Draft202012Validation, redefinesType]));
        Should.Throw<ArgumentException>(() => JsonSchemaDialect.Create(new Uri("dialect", UriKind.Relative), [JsonSchemaVocabulary.Draft202012Core]));
        Should.Throw<ArgumentNullException>(() => JsonSchemaDialect.Create(TestDialect.DialectId, [JsonSchemaVocabulary.Draft202012Core, null!]));
    }

    [Fact(DisplayName = DisplayPrefix + "Dialect: Should find keywords of every vocabulary it includes")]
    public void Dialect_TryGetKeyword_ShouldFindIncludedKeywords()
    {
        // Arrange
        JsonSchemaDialect dialect = TestDialect.Create(JsonSchemaKeywordDefinition.Create("x-even", _ => new EvenKeyword()));

        // Act & Assert
        dialect.Id.ShouldBe(TestDialect.DialectId);
        dialect.Version.ShouldBe(JsonSchemaVersion.Draft202012);
        dialect.TryGetKeyword("x-even", out JsonSchemaKeywordDefinition? even).ShouldBeTrue();
        even!.Name.ShouldBe("x-even");
        dialect.TryGetKeyword("unevaluatedItems", out _).ShouldBeTrue();
        dialect.TryGetKeyword("format", out _).ShouldBeFalse();
        JsonSchemaDialect.Draft07.TryGetKeyword("additionalItems", out _).ShouldBeTrue();
        JsonSchemaDialect.Draft202012.TryGetKeyword("additionalItems", out _).ShouldBeFalse();
        JsonSchemaDialect.Draft202012.TryGetKeyword("prefixItems", out _).ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => dialect.TryGetKeyword(null!, out _));
    }
}
