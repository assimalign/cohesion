using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownBlockTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: ATX headings render at their level")]
    public void Parse_AtxHeading_RendersAtLevel()
    {
        MarkdownText.ToHtml("# One").ShouldBe("<h1>One</h1>\n");
        MarkdownText.ToHtml("###### Six").ShouldBe("<h6>Six</h6>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: ATX closing sequences are stripped")]
    public void Parse_AtxClosingSequence_IsStripped()
    {
        MarkdownText.ToHtml("## Sub ##").ShouldBe("<h2>Sub</h2>\n");
        MarkdownText.ToHtml("# foo#").ShouldBe("<h1>foo#</h1>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: seven hashes or no space is not a heading")]
    public void Parse_InvalidAtx_IsParagraph()
    {
        MarkdownText.ToHtml("####### x").ShouldBe("<p>####### x</p>\n");
        MarkdownText.ToHtml("#hash").ShouldBe("<p>#hash</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: thematic breaks from all three characters")]
    public void Parse_ThematicBreak_AllMarkers()
    {
        MarkdownText.ToHtml("---").ShouldBe("<hr />\n");
        MarkdownText.ToHtml("***").ShouldBe("<hr />\n");
        MarkdownText.ToHtml("___").ShouldBe("<hr />\n");
        MarkdownText.ToHtml("- - -").ShouldBe("<hr />\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: paragraph lines join with soft breaks")]
    public void Parse_ParagraphContinuation_JoinsLines()
    {
        MarkdownText.ToHtml("one\ntwo").ShouldBe("<p>one\ntwo</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: blank lines separate paragraphs")]
    public void Parse_BlankLine_SeparatesParagraphs()
    {
        MarkdownText.ToHtml("one\n\ntwo").ShouldBe("<p>one</p>\n<p>two</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: fenced code preserves content and reports the language")]
    public void Parse_FencedCode_PreservesContent()
    {
        MarkdownText.ToHtml("```cs\nvar x = 1;\n```").ShouldBe("<pre><code class=\"language-cs\">var x = 1;\n</code></pre>\n");
        MarkdownText.ToHtml("```\n<&>\n```").ShouldBe("<pre><code>&lt;&amp;&gt;\n</code></pre>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: an unclosed fence runs to the end of input")]
    public void Parse_UnclosedFence_RunsToEnd()
    {
        MarkdownText.ToHtml("```\ncode").ShouldBe("<pre><code>code\n</code></pre>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: a longer fence closes a shorter one, not vice versa")]
    public void Parse_FenceLengths_MustMatchOrExceed()
    {
        MarkdownText.ToHtml("````\n```\n````").ShouldBe("<pre><code>```\n</code></pre>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: blank lines inside fences are content")]
    public void Parse_FenceBlankLines_AreContent()
    {
        MarkdownText.ToHtml("```\na\n\nb\n```").ShouldBe("<pre><code>a\n\nb\n</code></pre>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: block quotes contain blocks")]
    public void Parse_BlockQuote_ContainsBlocks()
    {
        MarkdownText.ToHtml("> quoted").ShouldBe("<blockquote>\n<p>quoted</p>\n</blockquote>\n");
        MarkdownText.ToHtml("> # h\n> p").ShouldBe("<blockquote>\n<h1>h</h1>\n<p>p</p>\n</blockquote>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: lazy continuation keeps a quoted paragraph open")]
    public void Parse_LazyContinuation_ContinuesQuote()
    {
        MarkdownText.ToHtml("> one\ntwo").ShouldBe("<blockquote>\n<p>one\ntwo</p>\n</blockquote>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: a blank line without a marker splits quotes")]
    public void Parse_BlankBetweenQuotes_SplitsQuotes()
    {
        MarkdownText.ToHtml("> a\n\n> b").ShouldBe(
            "<blockquote>\n<p>a</p>\n</blockquote>\n<blockquote>\n<p>b</p>\n</blockquote>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: a marked blank keeps one quote with two paragraphs")]
    public void Parse_MarkedBlank_KeepsQuoteOpen()
    {
        MarkdownText.ToHtml("> a\n>\n> b").ShouldBe("<blockquote>\n<p>a</p>\n<p>b</p>\n</blockquote>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: quotes nest")]
    public void Parse_NestedQuotes_Nest()
    {
        MarkdownText.ToHtml("> > deep").ShouldBe("<blockquote>\n<blockquote>\n<p>deep</p>\n</blockquote>\n</blockquote>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: tight bullet lists render bare items")]
    public void Parse_TightList_RendersBareItems()
    {
        MarkdownText.ToHtml("- a\n- b").ShouldBe("<ul>\n<li>a</li>\n<li>b</li>\n</ul>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: a blank between items makes the list loose")]
    public void Parse_LooseList_WrapsParagraphs()
    {
        MarkdownText.ToHtml("- a\n\n- b").ShouldBe(
            "<ul>\n<li>\n<p>a</p>\n</li>\n<li>\n<p>b</p>\n</li>\n</ul>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: a blank after the list does not loosen it")]
    public void Parse_TrailingBlank_KeepsListTight()
    {
        MarkdownText.ToHtml("- a\n- b\n\nafter").ShouldBe(
            "<ul>\n<li>a</li>\n<li>b</li>\n</ul>\n<p>after</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: ordered lists carry their start number")]
    public void Parse_OrderedList_CarriesStart()
    {
        MarkdownText.ToHtml("1. a\n2. b").ShouldBe("<ol>\n<li>a</li>\n<li>b</li>\n</ol>\n");
        MarkdownText.ToHtml("3. a\n4. b").ShouldBe("<ol start=\"3\">\n<li>a</li>\n<li>b</li>\n</ol>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: lists nest by content column")]
    public void Parse_NestedList_NestsByColumn()
    {
        MarkdownText.ToHtml("- a\n  - b").ShouldBe(
            "<ul>\n<li>a\n<ul>\n<li>b</li>\n</ul>\n</li>\n</ul>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: an item holds multiple blocks at its content column")]
    public void Parse_ItemWithTwoParagraphs_IsLoose()
    {
        MarkdownText.ToHtml("- a\n\n  b").ShouldBe(
            "<ul>\n<li>\n<p>a</p>\n<p>b</p>\n</li>\n</ul>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: changing the bullet marker starts a new list")]
    public void Parse_MarkerChange_StartsNewList()
    {
        MarkdownText.ToHtml("- a\n* b").ShouldBe(
            "<ul>\n<li>a</li>\n</ul>\n<ul>\n<li>b</li>\n</ul>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: only an ordered start of one interrupts a paragraph")]
    public void Parse_ListInterruption_RequiresStartOfOne()
    {
        MarkdownText.ToHtml("para\n1. x").ShouldBe("<p>para</p>\n<ol>\n<li>x</li>\n</ol>\n");
        MarkdownText.ToHtml("para\n2. x").ShouldBe("<p>para\n2. x</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: lazy continuation keeps an item paragraph open")]
    public void Parse_LazyItemContinuation_ContinuesParagraph()
    {
        MarkdownText.ToHtml("- one\ntwo").ShouldBe("<ul>\n<li>one\ntwo</li>\n</ul>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: containers compose inside list items")]
    public void Parse_ContainersInItems_Compose()
    {
        MarkdownText.ToHtml("- > q").ShouldBe("<ul>\n<li>\n<blockquote>\n<p>q</p>\n</blockquote>\n</li>\n</ul>\n");
        MarkdownText.ToHtml("> - a").ShouldBe("<blockquote>\n<ul>\n<li>a</li>\n</ul>\n</blockquote>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: a fence lives inside a list item")]
    public void Parse_FenceInItem_KeepsIndentation()
    {
        MarkdownText.ToHtml("- a\n  ```\n  x\n  ```").ShouldBe(
            "<ul>\n<li>a\n<pre><code>x\n</code></pre>\n</li>\n</ul>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: empty input produces an empty document")]
    public void Parse_EmptyInput_EmptyDocument()
    {
        MarkdownText.ToHtml(string.Empty).ShouldBe(string.Empty);
        MarkdownText.Parse(string.Empty).Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: excluded setext underlines stay in the paragraph")]
    public void Parse_SetextExcluded_DegradesPredictably()
    {
        MarkdownText.ToHtml("Title\n=====").ShouldBe("<p>Title\n=====</p>\n");
        MarkdownText.ToHtml("Title\n-----").ShouldBe("<p>Title</p>\n<hr />\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: excluded indented code degrades to a paragraph")]
    public void Parse_IndentedCodeExcluded_DegradesToParagraph()
    {
        MarkdownText.ToHtml("    code").ShouldBe("<p>code</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: excluded link reference definitions stay literal")]
    public void Parse_ReferenceDefinitionExcluded_StaysLiteral()
    {
        MarkdownText.ToHtml("[label]: /url").ShouldBe("<p>[label]: /url</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: excluded HTML blocks render escaped")]
    public void Parse_HtmlBlockExcluded_RendersEscaped()
    {
        MarkdownText.ToHtml("<div>x</div>").ShouldBe("<p>&lt;div&gt;x&lt;/div&gt;</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: tabs advance to four-column stops for structure")]
    public void Parse_Tabs_AdvanceToStops()
    {
        MarkdownText.ToHtml("-\ta").ShouldBe("<ul>\n<li>a</li>\n</ul>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Blocks: container depth beyond the cap degrades to text")]
    public void Parse_DepthCap_DegradesToText()
    {
        var markers = new string('>', 200).Replace(">", "> ");
        var html = MarkdownText.ToHtml(markers + "x");
        html.ShouldContain("x");
        MarkdownText.Parse(markers + "x").Count.ShouldBe(1);
    }
}
