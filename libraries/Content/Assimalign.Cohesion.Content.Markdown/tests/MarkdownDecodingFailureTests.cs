using System;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownDecodingFailureTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - RFC 3629 §3, Unicode 17.0 §3.9: Parse(ITextContent) preserves decoding failure")]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_StrictTextContent_ReportsContentFailure(bool utf16)
    {
        // Arrange. Authored invalid UTF-8 or unpaired UTF-16 high surrogate after a valid prefix.
        Encoding encoding = utf16 ? new UnicodeEncoding(false, false, true) : new UTF8Encoding(false, true);
        var prefix = encoding.GetBytes("text ");
        byte[] invalid = utf16 ? [0x00, 0xD8] : [0xC0, 0xAF];
        using var inner = MemoryContent.Create((byte[])[.. prefix, .. invalid]);
        using var content = TextContent.Create(inner, encoding);
        using var reader = content.OpenText();
        var expected = Should.Throw<ContentFormatException>(() => reader.ReadToEnd());

        // Act
        var actual = Should.Throw<ContentFormatException>(() => MarkdownText.Parse(content));

        // Assert
        actual.ShouldBeOfType<ContentFormatException>();
        actual.ErrorCode.ShouldBe(ContentFormatErrorCode.InvalidValue);
        actual.Position.ShouldBe(prefix.Length);
        actual.ErrorCode.ShouldBe(expected.ErrorCode);
        actual.Position.ShouldBe(expected.Position);
        actual.InnerException.ShouldBeOfType<DecoderFallbackException>();
    }
}
