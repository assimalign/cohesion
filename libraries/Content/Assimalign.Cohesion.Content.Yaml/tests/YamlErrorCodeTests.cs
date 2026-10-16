using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlErrorCodeTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Parse: malformed constructs use shared codes and absolute character positions")]
    [InlineData("[", ContentFormatErrorCode.Truncated)]
    [InlineData("[x", ContentFormatErrorCode.Truncated)]
    [InlineData("[x:", ContentFormatErrorCode.Truncated)]
    [InlineData("{x:", ContentFormatErrorCode.Truncated)]
    [InlineData("{", ContentFormatErrorCode.Truncated)]
    [InlineData("{x", ContentFormatErrorCode.Truncated)]
    [InlineData("\"", ContentFormatErrorCode.Truncated)]
    [InlineData("'", ContentFormatErrorCode.Truncated)]
    [InlineData("\"\\", ContentFormatErrorCode.Truncated)]
    [InlineData("\"\\u0", ContentFormatErrorCode.Truncated)]
    [InlineData("!<tag", ContentFormatErrorCode.Truncated)]
    [InlineData("\"\\q\"", ContentFormatErrorCode.InvalidValue)]
    [InlineData("\"\\uGGGG\"", ContentFormatErrorCode.InvalidValue)]
    [InlineData("\"\\uD800\"", ContentFormatErrorCode.InvalidValue)]
    [InlineData("\"\\UFFFFFFFF\"", ContentFormatErrorCode.InvalidValue)]
    [InlineData("\"\\U00110000\"", ContentFormatErrorCode.InvalidValue)]
    [InlineData("\"\\U0001F600\"", ContentFormatErrorCode.Unsupported)]
    [InlineData("%YAML 2.0\n---\nx", ContentFormatErrorCode.Unsupported)]
    [InlineData("\"multi\nline\": x", ContentFormatErrorCode.Unsupported)]
    [InlineData("[x y ] trailing", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("a: - x", ContentFormatErrorCode.InvalidStructure)]
    [InlineData("*missing", ContentFormatErrorCode.InvalidStructure)]
    public void Parse_MalformedConstruct_ReportsCode(string source, ContentFormatErrorCode code)
    {
        // Arrange / Act
        var exception = Should.Throw<YamlException>(() => YamlText.Parse(source));

        // Assert
        exception.ErrorCode.ShouldBe(code);
        exception.Position.ShouldNotBeNull();
        exception.Position!.Value.ShouldBeInRange(0, source.Length);
        exception.Line.ShouldBeGreaterThan(0);
        exception.Column.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - Parse: BOM, CRLF and Unicode do not shift syntax error offsets")]
    [InlineData("\uFEFF# é😀\r\n*missing", 8, 2, 1)]
    [InlineData("# é😀\r*missing", 6, 2, 1)]
    [InlineData("\uFEFF[\r\n  x", 7, 2, 4)]
    public void Parse_NormalizedSource_ReportsOriginalOffset(string source, long offset, int line, int column)
    {
        // Arrange / Act
        var exception = Should.Throw<YamlException>(() => YamlText.Parse(source));

        // Assert
        exception.Position.ShouldBe(offset);
        exception.Line.ShouldBe(line);
        exception.Column.ShouldBe(column);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - YamlException: compatibility constructor classifies structural failures")]
    public void Constructor_LegacyShape_UsesInvalidStructure()
    {
        // Arrange / Act
        var exception = new YamlException("authored test diagnostic", 2, 3);

        // Assert
        exception.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidStructure);
        exception.Position.ShouldBeNull();
        exception.Line.ShouldBe(2);
        exception.Column.ShouldBe(3);
    }
}
