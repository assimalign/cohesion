using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpMediaTypeStorageTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - Media types: Preserve canonical text and parameter storage")]
    [InlineData("application/json", "application/json", 0)]
    [InlineData("TEXT / PLAIN ; CHARSET=UTF-8", "text/plain; charset=UTF-8", 1)]
    [InlineData("text/plain; broken; =skip; bad/name=ignored; charset=utf-8; trailing", "text/plain; charset=utf-8", 1)]
    [InlineData("text/plain; note=\"a,b;c\"", "text/plain; note=\"a,b;c\"", 1)]
    [InlineData("text/plain; empty=\"\"", "text/plain; empty=\"\"", 1)]
    [InlineData("text/plain; note=\"a\\\"b\\\\c\"", "text/plain; note=\"a\\\"b\\\\c\"", 1)]
    [InlineData("multipart/form-data; boundary=first; BOUNDARY=second", "multipart/form-data; boundary=first; boundary=second", 2)]
    [InlineData("text/plain; junk; bad/name=ignored", "text/plain", 0)]
    [InlineData("*/*", "*/*", 0)]
    [InlineData("text/*", "text/*", 0)]
    [InlineData("application/vnd.api+json", "application/vnd.api+json", 0)]
    [InlineData("text/plain; note=\"\u00e9\"; charset=utf-8", "text/plain; note=\"\u00e9\"; charset=utf-8", 2)]
    public void ParseAndToString_ParameterShapes_ShouldPreserveValues(string raw, string expected, int count)
    {
        // Arrange / Act
        HttpMediaType parsed = HttpMediaType.Parse(raw);
        string actual = parsed.ToString();
        HttpMediaType reparsed = HttpMediaType.Parse(actual);

        // Assert: compare each parameter rather than assuming duplicate names are unique.
        actual.ShouldBe(expected);
        parsed.Parameters.Count.ShouldBe(count);
        reparsed.Type.ShouldBe(parsed.Type);
        reparsed.SubType.ShouldBe(parsed.SubType);
        reparsed.Parameters.Count.ShouldBe(count);
        for (int i = 0; i < count; i++)
        {
            reparsed.Parameters[i].Name.ShouldBe(parsed.Parameters[i].Name);
            reparsed.Parameters[i].Value.ShouldBe(parsed.Parameters[i].Value);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Media types: Default renders as empty text")]
    public void ToString_Default_ShouldReturnEmpty()
    {
        // Arrange
        HttpMediaType mediaType = default;

        // Act
        string text = mediaType.ToString();

        // Assert
        text.ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Media types: Long single parameters preserve their complete value")]
    public void ParseAndToString_LongSingleParameter_ShouldPreserveValue()
    {
        // Arrange
        string value = new('a', 1024);
        string raw = "text/plain; note=" + value;

        // Act
        HttpMediaType mediaType = HttpMediaType.Parse(raw);

        // Assert
        mediaType.Parameters.Count.ShouldBe(1);
        mediaType.Parameters[0].Value.ShouldBe(value);
        mediaType.ToString().ShouldBe(raw);
    }
}
