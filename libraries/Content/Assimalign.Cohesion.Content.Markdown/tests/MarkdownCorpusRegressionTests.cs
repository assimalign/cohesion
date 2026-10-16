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
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Lists: indentation is relative to current parent prefixes")]
    [InlineData("   > > 1.  one\n>>\n>>     two\n", "<blockquote>\n<blockquote>\n<ol>\n<li>\n<p>one</p>\n<p>two</p>\n</li>\n</ol>\n</blockquote>\n</blockquote>\n")]
    [InlineData(">>- one\n>>\n  >  > two\n", "<blockquote>\n<blockquote>\n<ul>\n<li>one</li>\n</ul>\n<p>two</p>\n</blockquote>\n</blockquote>\n")]
    public void Parse_VariableQuotePrefixes_PreservesListIndentation(string source, string expected)
    {
        // Arrange. CommonMark 259/260 isolate parent prefix width from item indentation.
        var document = MarkdownText.Parse(source);

        // Act
        var actual = MarkdownText.ToHtml(document);

        // Assert
        actual.ShouldBe(expected);
        MarkdownModelComparison.ShouldEqual(document, MarkdownText.Parse(MarkdownText.Write(document)), source);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Lists: nested blank lines loosen only their owning list")]
    [InlineData("- a\n  - b\n\n    c\n- d\n", true)]
    [InlineData("* a\n  > b\n  >\n* c\n", true)]
    [InlineData("- a\n  - b\n\n- c\n", false)]
    public void Parse_NestedBlankLine_TracksLocalLooseness(string source, bool tight)
    {
        // Arrange. Authored focused variants of CommonMark 319/320 and a trailing-blank control.
        var document = MarkdownText.Parse(source);

        // Act
        var list = document.Blocks[0].ShouldBeOfType<MarkdownList>();

        // Assert
        list.IsTight.ShouldBe(tight);
        MarkdownModelComparison.ShouldEqual(document, MarkdownText.Parse(MarkdownText.Write(document)), source);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Tabs: retain virtual columns across container prefixes")]
    [InlineData(" - foo\n   - bar\n\t - baz\n", "<ul>\n<li>foo\n<ul>\n<li>bar\n<ul>\n<li>baz</li>\n</ul>\n</li>\n</ul>\n</li>\n</ul>\n")]
    [InlineData("- ```\n \tcode\n  ```\n", "<ul>\n<li>\n<pre><code>  code\n</code></pre>\n</li>\n</ul>\n")]
    public void Parse_PartialTab_PreservesRemainingColumns(string source, string expected)
    {
        // Arrange. CommonMark 9 and an authored partial-tab fenced-code boundary.
        var document = MarkdownText.Parse(source);

        // Act
        var actual = MarkdownText.ToHtml(document);

        // Assert
        actual.ShouldBe(expected);
        MarkdownModelComparison.ShouldEqual(document, MarkdownText.Parse(MarkdownText.Write(document)), source);
    }
}
