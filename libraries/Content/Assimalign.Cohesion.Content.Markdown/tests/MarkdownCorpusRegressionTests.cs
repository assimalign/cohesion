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
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - HTML: URI brackets are percent encoded")]
    [InlineData("<https://example.test/[x]>", "<p><a href=\"https://example.test/%5Bx%5D\">https://example.test/[x]</a></p>\n")]
    [InlineData("[x](/a[b])", "<p><a href=\"/a%5Bb%5D\">x</a></p>\n")]
    public void ToHtml_BracketInDestination_UsesCommonMarkEscaping(string source, string expected)
    {
        // Arrange. Authored reductions of CommonMark 526/603; display text stays unencoded.
        var document = MarkdownText.Parse(source);

        // Act
        var actual = MarkdownText.ToHtml(document);

        // Assert
        actual.ShouldBe(expected);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Writer: literal whitespace and hashes retain model values")]
    [InlineData("foo&#10;&#10;bar")]
    [InlineData("&#9;foo")]
    [InlineData("## trailing \\#")]
    [InlineData("&#32;foo&#32;")]
    public void Write_StructuralLiteralCharacters_PreservesModel(string source)
    {
        // Arrange. Authored reductions of CommonMark 39, 40 and 76, plus entity-derived edge spaces.
        var expected = MarkdownText.Parse(source);

        // Act
        var written = MarkdownText.Write(expected);
        var actual = MarkdownText.Parse(written);

        // Assert
        MarkdownModelComparison.ShouldEqual(expected, actual, source);
        MarkdownText.Write(actual).ShouldBe(written);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Writer: all-space code spans do not gain padding")]
    [InlineData("` `")]
    [InlineData("`  `")]
    [InlineData("`   `")]
    [InlineData("` x `")]
    public void Write_SpaceOnlyCodeSpan_PreservesWidth(string source)
    {
        // Arrange. Authored whitespace widths exercise CommonMark 138/334 without encoder oracles.
        var expected = MarkdownText.Parse(source);

        // Act
        var written = MarkdownText.Write(expected);

        // Assert
        MarkdownModelComparison.ShouldEqual(expected, MarkdownText.Parse(written), source);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Writer: thematic breaks stay inside bullet items")]
    [InlineData("- Foo\n- * * *")]
    [InlineData("- - * * *")]
    public void Write_ListItemThematicBreak_PreservesContainer(string source)
    {
        // Arrange. Authored variants of CommonMark 61, including a nested item.
        var expected = MarkdownText.Parse(source);

        // Act
        var written = MarkdownText.Write(expected);

        // Assert
        MarkdownModelComparison.ShouldEqual(expected, MarkdownText.Parse(written), source);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Writer: adjacent lists remain separate models")]
    [InlineData("- foo\n- bar\n+ baz")]
    [InlineData("1. foo\n2. bar\n3) baz")]
    [InlineData("+ one\n- two\n* three")]
    public void Write_AdjacentLists_PreservesBoundaries(string source)
    {
        // Arrange. Authored variants of CommonMark 301/302 exercise two and three adjacent lists.
        var expected = MarkdownText.Parse(source);

        // Act
        var written = MarkdownText.Write(expected);
        var actual = MarkdownText.Parse(written);

        // Assert
        MarkdownModelComparison.ShouldEqual(expected, actual, source);
        MarkdownText.Write(actual).ShouldBe(written);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Fences: blank payload lines remove only item indentation")]
    [InlineData("- ```\n  x\n  \n  ```", "x\n\n")]
    [InlineData("- ```\n  x\n    \n  ```", "x\n  \n")]
    public void Parse_BlankFenceLine_PreservesPayloadWhitespace(string source, string payload)
    {
        // Arrange. Authored variants of CommonMark 318 distinguish prefix from payload spaces.
        var expected = MarkdownText.Parse(source);

        // Act
        var code = expected.Blocks[0].ShouldBeOfType<MarkdownList>().Items[0].Blocks[0].ShouldBeOfType<MarkdownCodeBlock>();

        // Assert
        code.Literal.ShouldBe(payload);
        MarkdownModelComparison.ShouldEqual(expected, MarkdownText.Parse(MarkdownText.Write(expected)), source);
    }
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Writer: link destinations and titles decode only once")]
    [InlineData("<https://example.test?find=\\*>")]
    [InlineData("<https://example.test/\\[\\>")]
    [InlineData("[x](foo&amp;amp; \"&amp;quot;\")")]
    [InlineData("[x](foo&#10;bar \"a&#10;&#10;b\")")]
    public void Write_LinkEscapes_PreservesDecodedValues(string source)
    {
        // Arrange. Authored reductions of CommonMark 20/603 plus entity and newline edge cases.
        var expected = MarkdownText.Parse(source);

        // Act
        var written = MarkdownText.Write(expected);
        var actual = MarkdownText.Parse(written);

        // Assert
        MarkdownModelComparison.ShouldEqual(expected, actual, source);
        MarkdownText.Write(actual).ShouldBe(written);
    }
}
