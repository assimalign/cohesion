using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownInlineTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: emphasis and strong emphasis")]
    public void Parse_Emphasis_RendersEmAndStrong()
    {
        MarkdownText.ToHtml("*em*").ShouldBe("<p><em>em</em></p>\n");
        MarkdownText.ToHtml("**strong**").ShouldBe("<p><strong>strong</strong></p>\n");
        MarkdownText.ToHtml("***both***").ShouldBe("<p><em><strong>both</strong></em></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: emphasis nests")]
    public void Parse_NestedEmphasis_Nests()
    {
        MarkdownText.ToHtml("*a **b** c*").ShouldBe("<p><em>a <strong>b</strong> c</em></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: underscores do not emphasize intraword")]
    public void Parse_IntrawordUnderscore_StaysLiteral()
    {
        MarkdownText.ToHtml("a_b_c").ShouldBe("<p>a_b_c</p>\n");
        MarkdownText.ToHtml("a*b*c").ShouldBe("<p>a<em>b</em>c</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: flanking rules reject space-padded delimiters")]
    public void Parse_SpacePaddedDelimiters_StayLiteral()
    {
        MarkdownText.ToHtml("a * b * c").ShouldBe("<p>a * b * c</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: the multiple-of-three rule prevents cross pairing")]
    public void Parse_MultipleOfThreeRule_Applies()
    {
        MarkdownText.ToHtml("*foo**bar**baz*").ShouldBe("<p><em>foo<strong>bar</strong>baz</em></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: code spans bind before emphasis")]
    public void Parse_CodeSpan_BindsFirst()
    {
        MarkdownText.ToHtml("`code`").ShouldBe("<p><code>code</code></p>\n");
        MarkdownText.ToHtml("*a `b*` c*").ShouldBe("<p><em>a <code>b*</code> c</em></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: code span fences match by exact length")]
    public void Parse_CodeSpanFences_MatchByLength()
    {
        MarkdownText.ToHtml("``a`b``").ShouldBe("<p><code>a`b</code></p>\n");
        MarkdownText.ToHtml("`unclosed").ShouldBe("<p>`unclosed</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: code spans strip one framing space pair")]
    public void Parse_CodeSpanFramingSpaces_StripOnce()
    {
        MarkdownText.ToHtml("` a `").ShouldBe("<p><code>a</code></p>\n");
        MarkdownText.ToHtml("`  `").ShouldBe("<p><code>  </code></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: code span line endings become spaces")]
    public void Parse_CodeSpanNewlines_BecomeSpaces()
    {
        MarkdownText.ToHtml("`a\nb`").ShouldBe("<p><code>a b</code></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: backslash escapes ASCII punctuation")]
    public void Parse_BackslashEscapes_ResolvePunctuation()
    {
        MarkdownText.ToHtml("\\*not\\*").ShouldBe("<p>*not*</p>\n");
        MarkdownText.ToHtml("\\\\").ShouldBe("<p>\\</p>\n");
        MarkdownText.ToHtml("\\a").ShouldBe("<p>\\a</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: numeric and predefined entities resolve")]
    public void Parse_Entities_ResolveRetainedSet()
    {
        MarkdownText.ToHtml("&amp; &#65; &#x41;").ShouldBe("<p>&amp; A A</p>\n");
        MarkdownText.ToHtml("&quot;q&quot;").ShouldBe("<p>&quot;q&quot;</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: unknown named entities stay literal")]
    public void Parse_UnknownEntity_StaysLiteral()
    {
        MarkdownText.ToHtml("&unknown;").ShouldBe("<p>&amp;unknown;</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: invalid numeric references become the replacement character")]
    public void Parse_InvalidNumericEntity_BecomesReplacement()
    {
        MarkdownText.ToHtml("&#0;").ShouldBe("<p>�</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: URI autolinks become links")]
    public void Parse_Autolink_BecomesLink()
    {
        MarkdownText.ToHtml("<https://example.com/a>").ShouldBe(
            "<p><a href=\"https://example.com/a\">https://example.com/a</a></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: angle text that is no autolink stays literal")]
    public void Parse_NonAutolink_StaysLiteral()
    {
        MarkdownText.ToHtml("<not a link>").ShouldBe("<p>&lt;not a link&gt;</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: inline links carry destination and title")]
    public void Parse_InlineLink_CarriesDestinationAndTitle()
    {
        MarkdownText.ToHtml("[t](/u)").ShouldBe("<p><a href=\"/u\">t</a></p>\n");
        MarkdownText.ToHtml("[t](/u \"T\")").ShouldBe("<p><a href=\"/u\" title=\"T\">t</a></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: angle destinations allow spaces")]
    public void Parse_AngleDestination_AllowsSpaces()
    {
        MarkdownText.ToHtml("[t](</u v>)").ShouldBe("<p><a href=\"/u%20v\">t</a></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: destinations may carry balanced parentheses")]
    public void Parse_BalancedParenDestination_Parses()
    {
        MarkdownText.ToHtml("[t](/u(1))").ShouldBe("<p><a href=\"/u(1)\">t</a></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: link text may contain emphasis")]
    public void Parse_LinkText_MayContainEmphasis()
    {
        MarkdownText.ToHtml("[*em*](/u)").ShouldBe("<p><a href=\"/u\"><em>em</em></a></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: links do not nest")]
    public void Parse_LinkInLink_OuterDegrades()
    {
        MarkdownText.ToHtml("[a [b](x)](y)").ShouldBe("<p>[a <a href=\"x\">b</a>](y)</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: images flatten their alternative text")]
    public void Parse_Image_FlattensAlt()
    {
        MarkdownText.ToHtml("![alt](img.png)").ShouldBe("<p><img src=\"img.png\" alt=\"alt\" /></p>\n");
        MarkdownText.ToHtml("![a *b*](x \"T\")").ShouldBe("<p><img src=\"x\" alt=\"a b\" title=\"T\" /></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: excluded reference links degrade to literal brackets")]
    public void Parse_ReferenceLinkExcluded_StaysLiteral()
    {
        MarkdownText.ToHtml("[a][b]").ShouldBe("<p>[a][b]</p>\n");
        MarkdownText.ToHtml("[a]").ShouldBe("<p>[a]</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: two trailing spaces make a hard break")]
    public void Parse_TrailingSpaces_MakeHardBreak()
    {
        MarkdownText.ToHtml("a  \nb").ShouldBe("<p>a<br />\nb</p>\n");
        MarkdownText.ToHtml("a \nb").ShouldBe("<p>a\nb</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: a backslash before the line ending makes a hard break")]
    public void Parse_BackslashLineEnd_MakesHardBreak()
    {
        MarkdownText.ToHtml("a\\\nb").ShouldBe("<p>a<br />\nb</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: excluded raw HTML renders escaped")]
    public void Parse_InlineHtmlExcluded_RendersEscaped()
    {
        MarkdownText.ToHtml("a <b>bold</b>").ShouldBe("<p>a &lt;b&gt;bold&lt;/b&gt;</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: null characters become the replacement character")]
    public void Parse_NullCharacter_BecomesReplacement()
    {
        MarkdownText.ToHtml("a\0b").ShouldBe("<p>a�b</p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Inlines: adversarial emphasis nesting renders without stack overflow")]
    public void Parse_DeepEmphasisNesting_RendersIteratively()
    {
        var depth = 5000;
        var text = string.Concat(System.Linq.Enumerable.Repeat("*a ", depth))
            + "x"
            + string.Concat(System.Linq.Enumerable.Repeat("*", depth));
        var html = MarkdownText.ToHtml(text);
        html.ShouldContain("x");

        var document = MarkdownText.Parse(text);
        MarkdownText.Write(document).ShouldContain("x");
    }
}
