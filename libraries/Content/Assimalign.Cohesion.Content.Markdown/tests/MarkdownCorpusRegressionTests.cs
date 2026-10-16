using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownCorpusRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Empty containers: preserve absence of paragraph and blank-item boundaries")]
    [InlineData(">\n>\n", "<blockquote>\n</blockquote>\n")]
    [InlineData(">\n> text\n", "<blockquote>\n<p>text</p>\n</blockquote>\n")]
    [InlineData("-\n  text\n", "<ul>\n<li>text</li>\n</ul>\n")]
    [InlineData("-\n\n  text\n", "<ul>\n<li></li>\n</ul>\n<p>text</p>\n")]
    [InlineData("- a\n-\n\n- c", "<ul>\n<li>\n<p>a</p>\n</li>\n<li></li>\n<li>\n<p>c</p>\n</li>\n</ul>\n")]
    public void Parse_EmptyContainer_DoesNotInventParagraph(string source, string expected)
    {
        // Arrange. Authored minimal regressions for CommonMark 239-241, 279-280 and 315.
        var document = MarkdownText.Parse(source);

        // Act
        var actual = MarkdownText.ToHtml(document);

        // Assert
        actual.ShouldBe(expected);
        MarkdownModelComparison.ShouldEqual(document, MarkdownText.Parse(MarkdownText.Write(document)), source);
    }
}
