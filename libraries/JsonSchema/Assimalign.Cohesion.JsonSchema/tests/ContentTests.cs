using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

public class ContentTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Content: ";

    private const string Schema = """
        {
          "contentEncoding": "base64",
          "contentMediaType": "application/json",
          "contentSchema": { "type": "object", "required": ["id"] }
        }
        """;

    private static readonly JsonSchemaEvaluationOptions _assert = new() { ContentAssertion = true };

    [Theory(DisplayName = DisplayPrefix + "Evaluate: Should only annotate content by default")]
    [InlineData("\"not base64!\"")]
    [InlineData("\"bm90IGpzb24=\"")]
    [InlineData("\"e30=\"")]
    public void Evaluate_Default_ShouldNotAssert(string instance)
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse(Schema);

        // Act & Assert
        document.IsValid(TestJson.Parse(instance)).ShouldBeTrue();
    }

    [Theory(DisplayName = DisplayPrefix + "Evaluate: Should decode, parse and validate content when asked")]
    [InlineData("\"eyJpZCI6IDF9\"", true)]
    [InlineData("\"not base64!\"", false)]
    [InlineData("\"bm90IGpzb24=\"", false)]
    [InlineData("\"e30=\"", false)]
    [InlineData("42", true)]
    public void Evaluate_ContentAssertion_ShouldAssert(string instance, bool expected)
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse(Schema);

        // Act & Assert
        document.IsValid(TestJson.Parse(instance), _assert).ShouldBe(expected);
    }

    [Fact(DisplayName = DisplayPrefix + "Evaluate: Should parse JSON content without an encoding")]
    public void Evaluate_MediaTypeOnly_ShouldParseString()
    {
        // Arrange
        JsonSchemaDocument document = JsonSchemaDocument.Parse("{\"contentMediaType\": \"application/json\", \"contentSchema\": {\"type\": \"array\"}}");

        // Act & Assert
        document.IsValid(TestJson.String("[1, 2]"), _assert).ShouldBeTrue();
        document.IsValid(TestJson.String("{}"), _assert).ShouldBeFalse();
        document.IsValid(TestJson.String("[1,"), _assert).ShouldBeFalse();
    }
}
