using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownRoundTripTests
{
    public static TheoryData<string> Documents => new()
    {
        "# Heading\n\nParagraph with *emphasis*, **strong**, and `code`.",
        "para one\npara one line two\n\npara two",
        "- a\n- b\n- c",
        "- a\n\n- b",
        "1. one\n2. two",
        "5. five\n6. six",
        "- outer\n  - inner\n  - inner2\n- outer2",
        "> quoted\n>\n> second",
        "> > nested\n\nafter",
        "```cs\nvar x = \"hi\";\n```",
        "````\n```\ninner fence\n```\n````",
        "---\n\ntext\n\n***",
        "[link](/url \"title\") and ![img](/pic.png)",
        "[t](</with space> \"q\\\"t\")",
        "a  \nhard break",
        "a\\\nbackslash break",
        "escape specials: \\* \\_ \\[ \\] \\` \\< \\&",
        "literal chars in text: a*b is 2*3 no wait",
        "`` code with ` backtick ``",
        "text with [unmatched bracket",
        "bang! and ![real](x)",
        "*em with `code` and [link](/u)*",
        "- tight item\n  continued lazily\n- second",
        "- item with para\n\n  and second para\n\n- next item",
        "- > quote in item\n- plain",
        "3) paren list",
        "Title\n=====",
        "    indented code degrade",
        "&#65;&amp;&unknown;",
        "<https://auto.link/x?y=1>",
    };

    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - RoundTrip: write is a fixed point and preserves rendering")]
    [MemberData(nameof(Documents))]
    public void WriteParse_IsFixedPointAndPreservesHtml(string source)
    {
        var document = MarkdownText.Parse(source);
        var html = MarkdownText.ToHtml(document);
        var written = MarkdownText.Write(document);

        var reparsed = MarkdownText.Parse(written);
        var rewritten = MarkdownText.Write(reparsed);

        MarkdownText.ToHtml(reparsed).ShouldBe(html, $"HTML drifted after round-trip of: {source}");
        rewritten.ShouldBe(written, $"canonical form is not a fixed point for: {source}");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - RoundTrip: canonical output normalizes markers")]
    public void Write_NormalizesMarkers()
    {
        MarkdownText.Write(MarkdownText.Parse("* star bullet")).ShouldBe("- star bullet\n");
        MarkdownText.Write(MarkdownText.Parse("3) paren")).ShouldBe("3. paren\n");
        MarkdownText.Write(MarkdownText.Parse("***")).ShouldBe("---\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - RoundTrip: nested emphasis alternates delimiters")]
    public void Write_NestedEmphasis_AlternatesDelimiters()
    {
        var written = MarkdownText.Write(MarkdownText.Parse("*a **b** c*"));
        written.ShouldBe("*a __b__ c*\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - RoundTrip: quote prefixes blank interior lines")]
    public void Write_QuoteBlankLines_KeepMarker()
    {
        var written = MarkdownText.Write(MarkdownText.Parse("> a\n>\n> b"));
        written.ShouldBe("> a\n>\n> b\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - RoundTrip: loose lists write blank separators")]
    public void Write_LooseList_WritesBlankSeparators()
    {
        var written = MarkdownText.Write(MarkdownText.Parse("- a\n\n- b"));
        written.ShouldBe("- a\n\n- b\n");
    }
}
