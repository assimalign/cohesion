using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownParserOptionsTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Parse: configured block depth degrades excess containers")]
    [InlineData("quotes", 1)]
    [InlineData("quotes", 2)]
    [InlineData("quotes", 128)]
    [InlineData("quotes", 256)]
    [InlineData("lists", 1)]
    [InlineData("lists", 2)]
    [InlineData("lists", 3)]
    [InlineData("lists", 128)]
    public void Parse_ContainerLimit_DegradesExcessMarkers(string shape, int limit)
    {
        // Arrange. Authored repeated markers; each list/item pair needs two depth units.
        var source = ParserBombProbe.CreateInput(shape, 1000);
        var options = new MarkdownParserOptions { MaxContainerDepth = limit };

        // Act
        var document = MarkdownText.Parse(source, options);
        var html = MarkdownText.ToHtml(document);

        // Assert
        ContainerDepth(document).ShouldBe(shape == "quotes" ? limit : limit / 2 * 2);
        html.ShouldContain("end", Case.Sensitive);
        html.ShouldContain(shape == "quotes" ? "&gt;" : "- ", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Parse: a full list depth still permits sibling items and a later root")]
    public void Parse_ContainersCloseAtLimit_PreservesSiblings()
    {
        // Arrange
        const string source = "- one\n- two\n\n# later";

        // Act
        var document = MarkdownText.Parse(source, new MarkdownParserOptions { MaxContainerDepth = 2 });

        // Assert
        document.Blocks.Count.ShouldBe(2);
        document.Blocks[0].ShouldBeOfType<MarkdownList>().Items.Count.ShouldBe(2);
        document.Blocks[1].ShouldBeOfType<MarkdownHeading>();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Parse: destination parenthesis limit has an exact boundary")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(64)]
    public void Parse_DestinationDepthBoundary_DegradesOnlyExcess(int limit)
    {
        // Arrange. Authored link destinations; outer syntax parentheses are not counted.
        var atLimit = "[end](u" + new string('(', limit) + "v" + new string(')', limit) + ")";
        var tooDeep = "[end](u" + new string('(', limit + 1) + "v" + new string(')', limit + 1) + ")";
        var options = new MarkdownParserOptions { MaxLinkDestinationDepth = limit };

        // Act / Assert
        MarkdownText.ToHtml(MarkdownText.Parse(atLimit, options)).ShouldContain("<a href=", Case.Sensitive);
        MarkdownText.ToHtml(MarkdownText.Parse(tooDeep, options)).ShouldNotContain("<a href=", Case.Sensitive);
        MarkdownText.ToHtml(MarkdownText.Parse("[end](<u(v)>)", new MarkdownParserOptions { MaxLinkDestinationDepth = 0 }))
            .ShouldContain("<a href=", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Parse: long inline delimiter runs remain accepted")]
    public void Parse_LongDelimiterRuns_PreservesInlineMeaning()
    {
        // Arrange. Author a code span whose delimiter has 100,000 backticks.
        var marker = new string('`', 100000);
        var source = "text " + marker + "end" + marker;

        // Act / Assert
        MarkdownText.ToHtml(MarkdownText.Parse(source, new MarkdownParserOptions { MaxContainerDepth = 1 }))
            .ShouldBe("<p>text <code>end</code></p>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Parse: every facade forwards limits")]
    public async Task Parse_AllEntryPoints_ApplyOptionsAsync()
    {
        // Arrange
        const string source = "> > > end";
        var options = new MarkdownParserOptions { MaxContainerDepth = 1 };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
        using var text = TextContent.Create(source);
        var reader = MarkdownText.CreateReader(options);
        var expected = MarkdownText.ToHtml(MarkdownText.Parse(source, options));

        // Act / Assert
        MarkdownText.ToHtml(MarkdownText.Parse(stream, options)).ShouldBe(expected);
        MarkdownText.ToHtml(MarkdownText.Parse(text, options)).ShouldBe(expected);
        stream.Position = 0;
        MarkdownText.ToHtml(reader.Read(stream)).ShouldBe(expected);
        stream.Position = 0;
        MarkdownText.ToHtml(await reader.ReadAsync(stream, CancellationToken.None)).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Parse: invalid resource options fail at composition")]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, -1)]
    public void Parse_InvalidOptions_ThrowsArgumentException(int containers, int destination)
    {
        // Arrange
        var options = new MarkdownParserOptions { MaxContainerDepth = containers, MaxLinkDestinationDepth = destination };

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => MarkdownText.CreateReader(options));
        Should.Throw<ArgumentOutOfRangeException>(() => MarkdownText.Parse("", options));
    }

    private static int ContainerDepth(MarkdownDocument document)
    {
        var pending = new Stack<(MarkdownNode Node, int Depth)>();
        pending.Push((document, 0));
        var maximum = 0;
        while (pending.TryPop(out var next))
        {
            maximum = Math.Max(maximum, next.Depth);
            switch (next.Node)
            {
                case MarkdownDocument root:
                    foreach (var block in root.Blocks) { pending.Push((block, next.Depth + (block is MarkdownBlockQuote or MarkdownList ? 1 : 0))); }
                    break;
                case MarkdownBlockQuote quote:
                    foreach (var block in quote.Blocks) { pending.Push((block, next.Depth + (block is MarkdownBlockQuote or MarkdownList ? 1 : 0))); }
                    break;
                case MarkdownList list:
                    foreach (var item in list.Items) { pending.Push((item, next.Depth + 1)); }
                    break;
                case MarkdownListItem item:
                    foreach (var block in item.Blocks) { pending.Push((block, next.Depth + (block is MarkdownBlockQuote or MarkdownList ? 1 : 0))); }
                    break;
            }
        }
        return maximum;
    }
}
