using System;
using System.Diagnostics;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

public class PatternTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Patterns: ";

    [Theory(DisplayName = DisplayPrefix + "pattern: Should follow ECMA-262 with Unicode semantics rather than .NET semantics")]
    [InlineData("^\\d+$", "123", true)]
    [InlineData("^\\d+$", "١٢٣", false)]
    [InlineData("^\\w+$", "abc_1", true)]
    [InlineData("^\\w+$", "é", false)]
    [InlineData("^\\s$", "\u00A0", true)]
    [InlineData("^a$", "a\n", false)]
    [InlineData("^.$", "\n", false)]
    [InlineData("^.$", "😀", true)]
    [InlineData("^\\p{L}+$", "héllo", true)]
    [InlineData("b", "abc", true)]
    [InlineData("^[^a]$", "😀", true)]
    public void Pattern_EcmaSemantics_ShouldMatch(string pattern, string value, bool expected)
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Create(TestJson.Parse("{\"pattern\": " + TestJson.String(pattern).GetRawText() + "}"));

        // Act
        bool valid = document.IsValid(TestJson.String(value));

        // Assert
        valid.ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "pattern: Should ignore instances that are not strings")]
    public void Pattern_NonString_ShouldPass()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"pattern\": \"^a$\"}");

        // Act & Assert
        document.IsValid(TestJson.Parse("1")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("[\"b\"]")).ShouldBeTrue();
    }

    [Theory(DisplayName = DisplayPrefix + "Load: Should refuse a pattern that is not an ECMA-262 regular expression")]
    [InlineData("{\"pattern\": \"(\"}")]
    [InlineData("{\"pattern\": \"*a\"}")]
    [InlineData("{\"pattern\": \"[b-a]\"}")]
    [InlineData("{\"pattern\": \"\\\\k<missing>(?<n>a)\"}")]
    [InlineData("{\"patternProperties\": {\"a{2,1}\": true}}")]
    public void Load_InvalidPattern_ShouldThrowInvalidRegularExpression(string json)
    {
        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => JsonSchemaDocument.Parse(json));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.InvalidRegularExpression);
        exception.Location.ShouldNotBeNull();
    }

    [Fact(DisplayName = DisplayPrefix + "pattern: Should match a pattern that needs no backtracking in linear time")]
    public void Pattern_NestedQuantifier_ShouldNotBacktrack()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"pattern\": \"^(a+)+$\"}", new JsonSchemaDocumentOptions { RegexMatchTimeout = TimeSpan.FromSeconds(5) });
        string input = new string('a', 64) + "!";
        Stopwatch stopwatch = Stopwatch.StartNew();

        // Act
        bool valid = document.IsValid(TestJson.String(input));

        // Assert
        valid.ShouldBeFalse();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = DisplayPrefix + "pattern: Should time out a backtracking pattern")]
    public void Pattern_BacktrackingFallback_ShouldTimeOut()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"pattern\": \"^(a+)+\\\\1$\"}", new JsonSchemaDocumentOptions { RegexMatchTimeout = TimeSpan.FromMilliseconds(50) });
        string input = new string('a', 64) + "!";

        // Act
        JsonSchemaException exception = Should.Throw<JsonSchemaException>(() => document.IsValid(TestJson.String(input)));

        // Assert
        exception.Code.ShouldBe(JsonSchemaErrorCode.RegularExpressionTimeout);
        exception.InnerException.ShouldBeOfType<System.Text.RegularExpressions.RegexMatchTimeoutException>();
    }

    [Fact(DisplayName = DisplayPrefix + "patternProperties: Should apply every matching pattern to a member")]
    public void PatternProperties_Members_ShouldApplyMatchingSchemas()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"patternProperties\": {\"^x-\": {\"type\": \"string\"}, \"id$\": {\"minLength\": 3}}, \"additionalProperties\": false}");

        // Act & Assert
        document.IsValid(TestJson.Parse("{\"x-id\": \"abc\"}")).ShouldBeTrue();
        document.IsValid(TestJson.Parse("{\"x-id\": \"ab\"}")).ShouldBeFalse();
        document.IsValid(TestJson.Parse("{\"x-name\": 1}")).ShouldBeFalse();
        document.IsValid(TestJson.Parse("{\"other\": 1}")).ShouldBeFalse();
    }
}
