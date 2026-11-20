using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

public class JsonSchemaEvaluationResultTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - JsonSchemaEvaluationResult: ";

    private const string PersonSchema = """
        {
          "$id": "https://example.com/person",
          "type": "object",
          "properties": {
            "name": { "type": "string", "title": "Name" },
            "age": { "type": "integer", "minimum": 0 }
          },
          "required": ["name"]
        }
        """;

    private static readonly JsonSchemaDocument _person = JsonSchemaDocument.Parse(PersonSchema);

    [Fact(DisplayName = DisplayPrefix + "Flag: Should report validity only")]
    public void Evaluate_Flag_ShouldWriteValidOnly()
    {
        // Act
        JsonSchemaEvaluationResult result = _person.Evaluate(TestJson.Parse("{\"name\": 5}"));

        // Assert
        result.IsValid.ShouldBeFalse();
        result.OutputFormat.ShouldBe(JsonSchemaOutputFormat.Flag);
        result.Errors.ShouldBeEmpty();
        result.Annotations.ShouldBeEmpty();
        result.ToString().ShouldBe("{\"valid\":false}");
    }

    [Fact(DisplayName = DisplayPrefix + "Basic: Should list every failed assertion with its locations")]
    public void Evaluate_BasicInvalid_ShouldListErrors()
    {
        // Act
        JsonSchemaEvaluationResult result = Evaluate(_person, "{\"name\": 5, \"age\": -1}", JsonSchemaOutputFormat.Basic);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.Annotations.ShouldBeEmpty();
        JsonSchemaEvaluationResult nameType = result.Errors.Single(error => error.KeywordLocation.Value == "/properties/name/type");
        nameType.InstanceLocation.Value.ShouldBe("/name");
        nameType.AbsoluteKeywordLocation.ShouldBe("https://example.com/person#/properties/name/type");
        nameType.Keyword.ShouldBe("type");
        nameType.Error.ShouldNotBeNullOrEmpty();
        nameType.Errors.ShouldBeEmpty();
        JsonSchemaEvaluationResult ageMinimum = result.Errors.Single(error => error.KeywordLocation.Value == "/properties/age/minimum");
        ageMinimum.InstanceLocation.Value.ShouldBe("/age");
        result.Errors.ShouldAllBe(error => !error.IsValid && error.Error != null);
    }

    [Fact(DisplayName = DisplayPrefix + "Basic: Should list the annotations of a valid evaluation")]
    public void Evaluate_BasicValid_ShouldListAnnotations()
    {
        // Act
        JsonSchemaEvaluationResult result = Evaluate(_person, "{\"name\": \"Ada\"}", JsonSchemaOutputFormat.Basic);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        JsonSchemaEvaluationResult title = result.Annotations.Single(unit => unit.KeywordLocation.Value == "/properties/name/title");
        title.InstanceLocation.Value.ShouldBe("/name");
        title.Annotation!.Value.GetString().ShouldBe("Name");
        JsonSchemaEvaluationResult properties = result.Annotations.Single(unit => unit.KeywordLocation.Value == "/properties");
        properties.Annotation!.Value.GetRawText().ShouldBe("[\"name\"]");
    }

    [Fact(DisplayName = DisplayPrefix + "Detailed: Should nest errors under the keyword that applied them")]
    public void Evaluate_DetailedInvalid_ShouldNestErrors()
    {
        // Act
        JsonSchemaEvaluationResult result = Evaluate(_person, "{\"name\": 5, \"age\": -1}", JsonSchemaOutputFormat.Detailed);

        // Assert
        result.IsValid.ShouldBeFalse();
        JsonSchemaEvaluationResult properties = result.Errors.ShouldHaveSingleItem();
        properties.Keyword.ShouldBe("properties");
        properties.Error.ShouldNotBeNullOrEmpty();
        properties.Errors.Select(error => error.KeywordLocation.Value).ShouldBe(["/properties/name/type", "/properties/age/minimum"], ignoreOrder: true);
    }

    [Fact(DisplayName = DisplayPrefix + "Verbose: Should report every keyword of a valid evaluation")]
    public void Evaluate_VerboseValid_ShouldReportEveryKeyword()
    {
        // Act
        JsonSchemaEvaluationResult result = Evaluate(_person, "{\"name\": \"Ada\", \"age\": 36}", JsonSchemaOutputFormat.Verbose);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.Schema.ShouldNotBeNull();
        result.Annotations.Select(unit => unit.Keyword).ShouldBe(["type", "properties", "required"], ignoreOrder: true);
        result.Annotations.ShouldAllBe(unit => unit.IsValid);
    }

    [Fact(DisplayName = DisplayPrefix + "Verbose: Should drop annotations inside a schema that failed")]
    public void Evaluate_VerboseInvalid_ShouldDropAnnotations()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"properties\": {\"a\": {\"title\": \"A\"}}, \"required\": [\"b\"]}");

        // Act
        JsonSchemaEvaluationResult result = Evaluate(document, "{\"a\": 1}", JsonSchemaOutputFormat.Verbose);

        // Assert
        result.IsValid.ShouldBeFalse();
        List<JsonSchemaEvaluationResult> units = Flatten(result).ToList();
        units.Single(unit => unit.Keyword == "title").Annotation.ShouldBeNull();
        units.ShouldAllBe(unit => unit.Annotation == null);
    }

    [Fact(DisplayName = DisplayPrefix + "Basic: Should continue the keyword location through a reference")]
    public void Evaluate_Reference_ShouldReportEvaluationPath()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"$id\": \"https://example.com/ref\", \"$defs\": {\"positive\": {\"minimum\": 0}}, \"properties\": {\"n\": {\"$ref\": \"#/$defs/positive\"}}}");

        // Act
        JsonSchemaEvaluationResult result = Evaluate(document, "{\"n\": -1}", JsonSchemaOutputFormat.Basic);

        // Assert
        JsonSchemaEvaluationResult minimum = result.Errors.Single(error => error.Keyword == "minimum");
        minimum.KeywordLocation.Value.ShouldBe("/properties/n/$ref/minimum");
        minimum.AbsoluteKeywordLocation.ShouldBe("https://example.com/ref#/$defs/positive/minimum");
        minimum.InstanceLocation.Value.ShouldBe("/n");
    }

    [Fact(DisplayName = DisplayPrefix + "Basic: Should omit absolute locations for a document without an identifier")]
    public void Evaluate_AnonymousDocument_ShouldOmitAbsoluteLocation()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"minimum\": 0}");

        // Act
        JsonSchemaEvaluationResult result = Evaluate(document, "-1", JsonSchemaOutputFormat.Basic);

        // Assert
        result.Errors.ShouldHaveSingleItem().AbsoluteKeywordLocation.ShouldBeNull();
        result.ToString().ShouldNotContain("absoluteKeywordLocation", Case.Sensitive);
    }

    [Fact(DisplayName = DisplayPrefix + "Errors: Should never repeat instance values")]
    public void Evaluate_InvalidInstance_ShouldNotEchoInstanceValues()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("""
            {
              "properties": {
                "secret": { "maxLength": 3, "pattern": "^x", "enum": ["a"], "const": "b", "format": "email", "type": "number" },
                "number": { "maximum": 10, "multipleOf": 7, "type": "string" }
              },
              "propertyNames": { "maxLength": 6 },
              "additionalProperties": false
            }
            """);
        JsonSchemaEvaluationOptions options = new() { OutputFormat = JsonSchemaOutputFormat.Verbose, FormatAssertion = true };

        // Act
        JsonSchemaEvaluationResult result = document.Evaluate(TestJson.Parse("{\"secret\": \"hunter2-password\", \"number\": 987654, \"intruder\": true}"), options);
        string output = result.ToString();

        // Assert
        result.IsValid.ShouldBeFalse();
        output.ShouldNotContain("hunter2-password", Case.Sensitive);
        output.ShouldNotContain("987654", Case.Sensitive);
        Flatten(result).Where(unit => unit.Error is not null).Select(unit => unit.Error!).ShouldAllBe(error => !error.Contains("intruder"));
    }

    [Fact(DisplayName = DisplayPrefix + "ToString: Should write the specification's output shape")]
    public void ToString_Basic_ShouldWriteSpecificationShape()
    {
        // Arrange
        JsonSchemaEvaluationResult result = Evaluate(_person, "{}", JsonSchemaOutputFormat.Basic);

        // Act
        using JsonDocument output = JsonDocument.Parse(result.ToString());

        // Assert
        JsonElement root = output.RootElement;
        root.GetProperty("valid").GetBoolean().ShouldBeFalse();
        root.GetProperty("keywordLocation").GetString().ShouldBe(string.Empty);
        root.GetProperty("instanceLocation").GetString().ShouldBe(string.Empty);
        JsonElement error = root.GetProperty("errors").EnumerateArray().Single();
        error.GetProperty("keywordLocation").GetString().ShouldBe("/required");
        error.GetProperty("absoluteKeywordLocation").GetString().ShouldBe("https://example.com/person#/required");
        error.GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
        root.TryGetProperty("annotations", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "WriteTo: Should refuse a null writer")]
    public void WriteTo_NullWriter_ShouldThrowArgumentNullException()
    {
        // Arrange
        JsonSchemaEvaluationResult result = _person.Evaluate(TestJson.Parse("{}"));

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => result.WriteTo(null!));
    }

    [Fact(DisplayName = DisplayPrefix + "IsValid: Should agree with every output format")]
    public void IsValid_EveryOutputFormat_ShouldAgree()
    {
        // Arrange
        string[] instances = ["{\"name\": \"Ada\"}", "{\"name\": 1}", "{}", "[]", "{\"name\": \"x\", \"age\": 1.5}"];

        foreach (string instance in instances)
        {
            // Act
            bool expected = _person.IsValid(TestJson.Parse(instance));

            // Assert
            foreach (JsonSchemaOutputFormat format in Enum.GetValues<JsonSchemaOutputFormat>())
            {
                Evaluate(_person, instance, format).IsValid.ShouldBe(expected, instance + " " + format);
            }
        }
    }

    private static JsonSchemaEvaluationResult Evaluate(JsonSchemaDocument document, string instance, JsonSchemaOutputFormat format) =>
        document.Evaluate(TestJson.Parse(instance), new JsonSchemaEvaluationOptions { OutputFormat = format });

    private static IEnumerable<JsonSchemaEvaluationResult> Flatten(JsonSchemaEvaluationResult result)
    {
        yield return result;
        foreach (JsonSchemaEvaluationResult child in result.Errors.Concat(result.Annotations))
        {
            foreach (JsonSchemaEvaluationResult descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
