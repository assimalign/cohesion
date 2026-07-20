using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

/// <summary>
/// Cases transcribed from the CommonMark 0.31.2 specification's examples
/// (https://spec.commonmark.org/0.31.2/, CC-BY-SA 4.0), restricted to the retained subset: each
/// entry names the spec section it derives from. The full spec corpus harness is tracked
/// separately (#469); these pin the subset's spec-faithful behavior at the construct level.
/// </summary>
public class MarkdownSpecCorpusTests
{
    public static TheoryData<string, string, string> Examples => new()
    {
        // Thematic breaks (§4.1)
        { "***\n---\n___", "<hr />\n<hr />\n<hr />\n", "4.1 Thematic breaks" },
        { " ---", "<hr />\n", "4.1 Thematic breaks (indent up to three)" },
        { "_ _ _ _ _", "<hr />\n", "4.1 Thematic breaks (interior spaces)" },

        // ATX headings (§4.2)
        { "# foo\n## foo\n### foo", "<h1>foo</h1>\n<h2>foo</h2>\n<h3>foo</h3>\n", "4.2 ATX headings" },
        { "#\n## \n###", "<h1></h1>\n<h2></h2>\n<h3></h3>\n", "4.2 ATX headings (empty)" },
        { "# foo *bar*", "<h1>foo <em>bar</em></h1>\n", "4.2 ATX headings (inline content)" },

        // Fenced code blocks (§4.5)
        { "```\n<\n >\n```", "<pre><code>&lt;\n &gt;\n</code></pre>\n", "4.5 Fenced code blocks" },
        { "```ruby\ndef foo(x)\n  return 3\nend\n```", "<pre><code class=\"language-ruby\">def foo(x)\n  return 3\nend\n</code></pre>\n", "4.5 Fenced code blocks (info string)" },

        // Paragraphs (§4.8)
        { "aaa\n\nbbb", "<p>aaa</p>\n<p>bbb</p>\n", "4.8 Paragraphs" },
        { "aaa\n bbb", "<p>aaa\nbbb</p>\n", "4.8 Paragraphs (leading spaces stripped)" },

        // Block quotes (§5.1)
        { "> # Foo\n> bar\n> baz", "<blockquote>\n<h1>Foo</h1>\n<p>bar\nbaz</p>\n</blockquote>\n", "5.1 Block quotes" },
        { "> bar\nbaz", "<blockquote>\n<p>bar\nbaz</p>\n</blockquote>\n", "5.1 Block quotes (lazy)" },

        // List items and lists (§5.2, §5.3)
        { "- one\n- two", "<ul>\n<li>one</li>\n<li>two</li>\n</ul>\n", "5.3 Lists" },
        { "1. one\n2. two", "<ol>\n<li>one</li>\n<li>two</li>\n</ol>\n", "5.3 Lists (ordered)" },
        { "- foo\n- bar\n+ baz", "<ul>\n<li>foo</li>\n<li>bar</li>\n</ul>\n<ul>\n<li>baz</li>\n</ul>\n", "5.3 Lists (marker change)" },
        { "- foo\n\n- bar\n\n\n- baz", "<ul>\n<li>\n<p>foo</p>\n</li>\n<li>\n<p>bar</p>\n</li>\n<li>\n<p>baz</p>\n</li>\n</ul>\n", "5.3 Lists (loose)" },

        // Backslash escapes (§2.4)
        { "\\*not emphasized*", "<p>*not emphasized*</p>\n", "2.4 Backslash escapes" },
        { "\\→\\A\\a\\ \\3\\φ\\«", "<p>\\→\\A\\a\\ \\3\\φ\\«</p>\n", "2.4 Backslash escapes (non-punctuation)" },

        // Entity references (§2.5, retained subset)
        { "&#35; &#1234; &#x22; &#XD06;", "<p># Ӓ &quot; ആ</p>\n", "2.5 Entity references (numeric)" },

        // Code spans (§6.1)
        { "`foo`", "<p><code>foo</code></p>\n", "6.1 Code spans" },
        { "`` foo ` bar ``", "<p><code>foo ` bar</code></p>\n", "6.1 Code spans (double fence)" },
        { "`foo\\`bar`", "<p><code>foo\\</code>bar`</p>\n", "6.1 Code spans (no escapes inside)" },

        // Emphasis (§6.2)
        { "*foo bar*", "<p><em>foo bar</em></p>\n", "6.2 Emphasis" },
        { "a * foo bar*", "<p>a * foo bar*</p>\n", "6.2 Emphasis (not left-flanking)" },
        { "foo*bar*", "<p>foo<em>bar</em></p>\n", "6.2 Emphasis (intraword star)" },
        { "foo_bar_", "<p>foo_bar_</p>\n", "6.2 Emphasis (no intraword underscore)" },
        { "**foo bar**", "<p><strong>foo bar</strong></p>\n", "6.2 Strong emphasis" },
        { "*foo**bar**baz*", "<p><em>foo<strong>bar</strong>baz</em></p>\n", "6.2 Emphasis (rule of three)" },
        { "*foo **bar** baz*", "<p><em>foo <strong>bar</strong> baz</em></p>\n", "6.2 Emphasis (nested strong)" },

        // Links (§6.3)
        { "[link](/uri \"title\")", "<p><a href=\"/uri\" title=\"title\">link</a></p>\n", "6.3 Links" },
        { "[link]()", "<p><a href=\"\">link</a></p>\n", "6.3 Links (empty destination)" },
        { "[link](/my uri)", "<p>[link](/my uri)</p>\n", "6.3 Links (unescaped space fails)" },
        { "[link](foo(and(bar)))", "<p><a href=\"foo(and(bar))\">link</a></p>\n", "6.3 Links (balanced parens)" },

        // Images (§6.4)
        { "![foo](/url \"title\")", "<p><img src=\"/url\" alt=\"foo\" title=\"title\" /></p>\n", "6.4 Images" },

        // Autolinks (§6.5)
        { "<http://foo.bar.baz>", "<p><a href=\"http://foo.bar.baz\">http://foo.bar.baz</a></p>\n", "6.5 Autolinks" },
        { "<mailto:foo@bar.example.com>", "<p><a href=\"mailto:foo@bar.example.com\">mailto:foo@bar.example.com</a></p>\n", "6.5 Autolinks (mailto scheme)" },

        // Hard line breaks (§6.7)
        { "foo  \nbaz", "<p>foo<br />\nbaz</p>\n", "6.7 Hard line breaks" },
        { "foo\\\nbaz", "<p>foo<br />\nbaz</p>\n", "6.7 Hard line breaks (backslash)" },

        // Soft line breaks (§6.8)
        { "foo\nbaz", "<p>foo\nbaz</p>\n", "6.8 Soft line breaks" },
    };

    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Corpus: spec-derived examples for the retained subset")]
    [MemberData(nameof(Examples))]
    public void ToHtml_SpecDerivedExample_Matches(string markdown, string expected, string section)
    {
        MarkdownText.ToHtml(markdown).ShouldBe(expected, section);
    }
}
