using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Yaml.Tests;

public class YamlDecodingFailureTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Yaml] - RFC 3629 §3, Unicode 17.0 §3.9: Parse(ITextContent) preserves decoding failure")]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_StrictTextContent_ReportsContentFailure(bool utf16)
    {
        // Arrange. Authored malformed scalar bytes after valid YAML key/separation bytes.
        Encoding encoding = utf16 ? new UnicodeEncoding(false, false, true) : new UTF8Encoding(false, true);
        var prefix = encoding.GetBytes("key: ");
        byte[] invalid = utf16 ? [0x00, 0xD8] : [0xC0, 0xAF];
        using var inner = MemoryContent.Create((byte[])[.. prefix, .. invalid]);
        using var content = TextContent.Create(inner, encoding);
        using var reader = content.OpenText();
        var expected = Should.Throw<ContentFormatException>(() => reader.ReadToEnd());

        // Act
        var actual = Should.Throw<ContentFormatException>(() => YamlText.Parse(content));

        // Assert
        actual.ShouldBeOfType<ContentFormatException>();
        actual.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        actual.Position.ShouldBe(prefix.Length);
        actual.ErrorCode.ShouldBe(expected.ErrorCode);
        actual.Position.ShouldBe(expected.Position);
        actual.InnerException.ShouldBeOfType<DecoderFallbackException>();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Yaml] - RFC 3629 §3: lenient malformed UTF-8 retains replacement characters")]
    public void Parse_LenientTextContent_PreservesReplacement()
    {
        // Arrange. Hand-built invalid UTF-8 stays a YAML string on the lenient path.
        using var inner = MemoryContent.Create((byte[])[.. "key: "u8, 0xC0, 0xAF]);
        using var content = TextContent.Create(inner, new UTF8Encoding(false));

        // Act
        var document = YamlText.Parse(content);

        // Assert
        YamlText.Write(document).ShouldBe("key: \uFFFD\uFFFD\n");
    }
}
