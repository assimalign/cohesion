using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownMalformedCorpusTests
{
    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - Hostile authored corpus: bounded parse and render", Timeout = 15000)]
    [InlineData("quotes")]
    [InlineData("lists")]
    [InlineData("stars")]
    [InlineData("underscores")]
    [InlineData("backticks")]
    [InlineData("open brackets")]
    [InlineData("closed brackets without destinations")]
    public async Task Parse_PathologicalInput_CompletesWithinBoundAsync(string kind)
    {
        // Arrange. Authored adversarial families, not downloaded examples. A generous wall-clock
        // bound avoids treating this correctness guard as a throughput benchmark on shared CI.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));

        // Act / Assert. Geometrically growing sizes exercise the bound at 4K, 16K and 64K markers.
        await Task.Run(() =>
        {
            foreach (var count in new[] { 4096, 16384, 65536 })
            {
                timeout.Token.ThrowIfCancellationRequested();
                var source = kind switch
                {
                    "quotes" => Repeat("> ", count) + "end",
                    "lists" => Repeat("- ", count) + "end",
                    "stars" => new string('*', count) + "end" + new string('*', count),
                    "underscores" => new string('_', count) + "end" + new string('_', count),
                    "backticks" => "a " + new string('`', count) + " end",
                    "open brackets" => new string('[', count) + "end",
                    _ => Repeat("[x]", count) + "end",
                };
                var document = MarkdownText.Parse(source);
                document.Blocks.ShouldNotBeEmpty();
                var html = MarkdownText.ToHtml(document);
                html.ShouldContain("end", Case.Sensitive);
                if (kind is "quotes" or "lists")
                {
                    CountContainers(document).ShouldBeLessThanOrEqualTo(128);
                }
            }
        }, timeout.Token).WaitAsync(timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Markdown] - RFC 3629 §3/§4: invalid UTF-8 keeps lenient replacement contract")]
    [InlineData("C080", "\uFFFD\uFFFD")]
    [InlineData("EDA080", "\uFFFD\uFFFD\uFFFD")]
    [InlineData("F09F98", "\uFFFD")]
    [InlineData("C0C1F5F6F7F8F9FAFBFCFDFEFF", "\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD\uFFFD")]
    public void Parse_InvalidUtf8_ReplacesExactly(string hex, string replacement)
    {
        // Arrange. Hand-built malformed bytes occur inside an otherwise valid Markdown paragraph.
        byte[] bytes = [.. "text "u8, .. Convert.FromHexString(hex)];
        using var stream = new MemoryStream(bytes);
        using var inner = MemoryContent.Create(bytes);
        using var content = TextContent.Create(inner, Encoding.UTF8);

        // Act
        var fromStream = MarkdownText.Parse(stream);
        var fromText = MarkdownText.Parse(content);

        // Assert
        MarkdownText.ToHtml(fromStream).ShouldBe("<p>text " + replacement + "</p>\n");
        MarkdownModelComparison.ShouldEqual(fromStream, fromText, hex);
    }

    private static string Repeat(string value, int count)
    {
        var builder = new StringBuilder(value.Length * count);
        for (var index = 0; index < count; index++)
        {
            builder.Append(value);
        }

        return builder.ToString();
    }

    private static int CountContainers(MarkdownDocument document)
    {
        var pending = new System.Collections.Generic.Stack<MarkdownNode>();
        pending.Push(document);
        var count = 0;
        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case MarkdownDocument root: foreach (var block in root.Blocks) { pending.Push(block); } break;
                case MarkdownBlockQuote quote:
                    count++; foreach (var block in quote.Blocks) { pending.Push(block); } break;
                case MarkdownList list:
                    count++; foreach (var item in list.Items) { pending.Push(item); } break;
                case MarkdownListItem item:
                    count++; foreach (var block in item.Blocks) { pending.Push(block); } break;
            }
        }

        return count;
    }
}
