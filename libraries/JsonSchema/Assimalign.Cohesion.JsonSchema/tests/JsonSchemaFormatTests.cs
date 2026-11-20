using System;
using System.Text.Json;
using System.Threading;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

public class JsonSchemaFormatTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - JsonSchemaFormat: ";

    private const string FormatAssertionMetaSchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$vocabulary": {
            "https://json-schema.org/draft/2020-12/vocab/core": true,
            "https://json-schema.org/draft/2020-12/vocab/format-assertion": true
          },
          "$dynamicAnchor": "meta"
        }
        """;

    [Fact(DisplayName = DisplayPrefix + "Create: Should refuse a null or empty name and a null predicate")]
    public void Create_InvalidArguments_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => JsonSchemaFormat.Create(null!, _ => true));
        Should.Throw<ArgumentException>(() => JsonSchemaFormat.Create(string.Empty, _ => true));
        Should.Throw<ArgumentNullException>(() => JsonSchemaFormat.Create("x-format", null!));
    }

    [Fact(DisplayName = DisplayPrefix + "IsValid: Should pass every instance that is not a string without calling the predicate")]
    public void IsValid_NonString_ShouldPass()
    {
        // Arrange
        int calls = 0;
        JsonSchemaFormat format = JsonSchemaFormat.Create("never", _ =>
        {
            calls++;
            return false;
        });

        // Act & Assert
        format.Name.ShouldBe("never");
        format.IsValid(TestJson.Parse("1")).ShouldBeTrue();
        format.IsValid(TestJson.Parse("{}")).ShouldBeTrue();
        format.IsValid(TestJson.Parse("null")).ShouldBeTrue();
        format.IsValid(TestJson.Parse("\"text\"")).ShouldBeFalse();
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = DisplayPrefix + "IsValid: Should give the predicate the decoded string, however long")]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(5000)]
    public void IsValid_EscapedString_ShouldDecode(int length)
    {
        // Arrange
        string expected = new string('a', length) + "é\"\\/\u2028😀";
        string? received = null;
        JsonSchemaFormat format = JsonSchemaFormat.Create("capture", text =>
        {
            received = text.ToString();
            return true;
        });
        JsonElement instance = TestJson.Parse("\"" + new string('a', length) + "\\u00e9\\\"\\\\\\/\\u2028\\ud83d\\ude00\"");

        // Act
        bool valid = format.IsValid(instance);

        // Assert
        valid.ShouldBeTrue();
        received.ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should let a custom format replace a built-in one")]
    public void Evaluate_CustomFormat_ShouldReplaceBuiltIn()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"format\": \"email\"}");
        JsonElement instance = TestJson.Parse("\"not an address\"");
        JsonSchemaEvaluationOptions builtIn = new() { FormatAssertion = true };
        JsonSchemaEvaluationOptions replaced = new() { FormatAssertion = true, Formats = [JsonSchemaFormat.Create("email", _ => true)] };

        // Act & Assert
        document.IsValid(instance, builtIn).ShouldBeFalse();
        document.IsValid(instance, replaced).ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should only annotate when format assertion is off")]
    public void Evaluate_AssertionOff_ShouldNotCallFormat()
    {
        // Arrange
        int calls = 0;
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"format\": \"x-strict\"}");
        JsonSchemaEvaluationOptions options = new()
        {
            OutputFormat = JsonSchemaOutputFormat.Basic,
            Formats = [JsonSchemaFormat.Create("x-strict", _ =>
            {
                Interlocked.Increment(ref calls);
                return false;
            })],
        };

        // Act
        JsonSchemaEvaluationResult result = document.Evaluate(TestJson.Parse("\"value\""), options);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.Annotations.ShouldHaveSingleItem().Annotation!.Value.GetString().ShouldBe("x-strict");
        calls.ShouldBe(0);
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should pass an unknown format under the assertion option")]
    public void Evaluate_UnknownFormatUnderOption_ShouldPass()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"format\": \"x-unknown\"}");

        // Act
        bool valid = document.IsValid(TestJson.Parse("\"anything\""), new JsonSchemaEvaluationOptions { FormatAssertion = true });

        // Assert
        valid.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should assert formats and fail unknown ones under the format-assertion vocabulary")]
    public void Evaluate_FormatAssertionVocabulary_ShouldAssertAndFailUnknown()
    {
        // Arrange
        JsonSchemaRegistry registry = new();
        registry.Register(new Uri("https://example.com/meta/format-assertion"), TestJson.Parse(FormatAssertionMetaSchema));
        JsonSchemaDocumentOptions options = new() { Registry = registry };
        JsonSchemaDocument known = JsonSchemaDocument.Parse("{\"$schema\": \"https://example.com/meta/format-assertion\", \"format\": \"ipv4\"}", options);
        JsonSchemaDocument unknown = JsonSchemaDocument.Parse("{\"$schema\": \"https://example.com/meta/format-assertion\", \"format\": \"x-unknown\"}", options);
        JsonSchemaEvaluationOptions custom = new() { Formats = [JsonSchemaFormat.Create("x-unknown", text => text.StartsWith("ok"))] };

        // Act & Assert
        known.IsValid(TestJson.Parse("\"192.168.0.1\"")).ShouldBeTrue();
        known.IsValid(TestJson.Parse("\"192.168.0\"")).ShouldBeFalse();
        unknown.IsValid(TestJson.Parse("\"ok\"")).ShouldBeFalse();
        unknown.IsValid(TestJson.Parse("\"ok\""), custom).ShouldBeTrue();
        unknown.IsValid(TestJson.Parse("\"no\""), custom).ShouldBeFalse();
        unknown.IsValid(TestJson.Parse("12")).ShouldBeFalse();
    }

    [Fact(DisplayName = DisplayPrefix + "Options: Should copy the formats and use the last of two with one name")]
    public void Options_Formats_ShouldSnapshotAndPreferLast()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"format\": \"x-twice\"}");
        IJsonSchemaFormat[] formats = [JsonSchemaFormat.Create("x-twice", _ => false), JsonSchemaFormat.Create("x-twice", _ => true)];
        JsonSchemaEvaluationOptions options = new() { FormatAssertion = true, Formats = formats };

        // Act
        formats[1] = JsonSchemaFormat.Create("x-twice", _ => false);
        bool valid = document.IsValid(TestJson.Parse("\"value\""), options);

        // Assert
        valid.ShouldBeTrue();
        Should.Throw<ArgumentNullException>(() => new JsonSchemaEvaluationOptions { Formats = [null!] });
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should report an invalid format in the output")]
    public void Evaluate_InvalidFormat_ShouldReportError()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"format\": \"date\"}");

        // Act
        JsonSchemaEvaluationResult result = document.Evaluate(TestJson.Parse("\"2021-02-29\""), new JsonSchemaEvaluationOptions { FormatAssertion = true, OutputFormat = JsonSchemaOutputFormat.Basic });

        // Assert
        result.IsValid.ShouldBeFalse();
        JsonSchemaEvaluationResult error = result.Errors.ShouldHaveSingleItem();
        error.Keyword.ShouldBe("format");
        error.Error.ShouldNotBeNull().ShouldContain("date", Case.Sensitive);
        error.Error.ShouldNotContain("2021-02-29", Case.Sensitive);
    }
}
