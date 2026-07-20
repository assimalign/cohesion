using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Content;
using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

public class MarkdownModelTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Model: heading levels validate one through six")]
    public void Heading_InvalidLevel_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new MarkdownHeading(0));
        Should.Throw<ArgumentOutOfRangeException>(() => new MarkdownHeading(7));
        new MarkdownHeading(3).Level.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Model: value carriers reject null")]
    public void Nodes_NullValues_Throw()
    {
        Should.Throw<ArgumentNullException>(() => new MarkdownLiteral(null!));
        Should.Throw<ArgumentNullException>(() => new MarkdownCodeSpan(null!));
        Should.Throw<ArgumentNullException>(() => new MarkdownLink(null!));
        Should.Throw<ArgumentNullException>(() => new MarkdownImage(null!));
        Should.Throw<ArgumentOutOfRangeException>(() => new MarkdownList(isOrdered: true) { Start = -1 });
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Model: the facade validates arguments")]
    public void Facade_NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => MarkdownText.Parse((string)null!));
        Should.Throw<ArgumentNullException>(() => MarkdownText.Parse((Stream)null!));
        Should.Throw<ArgumentNullException>(() => MarkdownText.Parse((ITextContent)null!));
        Should.Throw<ArgumentNullException>(() => MarkdownText.ToHtml((MarkdownDocument)null!));
        Should.Throw<ArgumentNullException>(() => MarkdownText.Write((MarkdownDocument)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Model: the format descriptor names the baseline")]
    public void Format_NamesTheBaseline()
    {
        MarkdownText.Format.Name.ShouldBe("Markdown");
        MarkdownText.Format.MediaTypes.ShouldContain("text/markdown");
        MarkdownText.Format.FileExtensions.ShouldContain(".md");
        MarkdownText.Format.Specification.ShouldNotBeNull();
        MarkdownText.Format.Specification.ShouldContain("commonmark", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Model: stream parsing detects encoding")]
    public void Parse_Stream_DetectsEncoding()
    {
        using var stream = new MemoryStream(Encoding.Unicode.GetBytes("# Wide"));
        var document = MarkdownText.Parse(stream);
        MarkdownText.ToHtml(document).ShouldBe("<h1>Wide</h1>\n");
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Model: text content parsing reads through the text layer")]
    public void Parse_TextContent_Reads()
    {
        using var content = TextContentFactory.FromString("- a\n- b");
        var document = MarkdownText.Parse(content);
        document.Count.ShouldBe(1);
        document[0].ShouldBeOfType<MarkdownList>().Items.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Model: the content seams round-trip through streams")]
    public async Task Seams_RoundTripThroughStreams()
    {
        var document = MarkdownText.Parse("# T\n\nbody *em*");

        using var buffer = new MemoryStream();
        MarkdownText.CreateWriter().Write(buffer, document);
        buffer.Position = 0;

        var read = MarkdownText.CreateReader().Read(buffer);
        MarkdownText.ToHtml(read).ShouldBe(MarkdownText.ToHtml(document));

        buffer.Position = 0;
        var readAsync = await MarkdownText.CreateReader().ReadAsync(buffer, CancellationToken.None);
        MarkdownText.ToHtml(readAsync).ShouldBe(MarkdownText.ToHtml(document));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Model: seam operations honor pre-cancelled tokens")]
    public async Task Seams_PreCancelledToken_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        using var buffer = new MemoryStream();
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await MarkdownText.CreateReader().ReadAsync(buffer, cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await MarkdownText.CreateWriter().WriteAsync(buffer, new MarkdownDocument(), cancelled.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Content.Markdown] - Model: documents build programmatically and render")]
    public void Document_BuiltByHand_Renders()
    {
        var document = new MarkdownDocument();
        var heading = new MarkdownHeading(2);
        heading.Inlines.Add(new MarkdownLiteral("Built"));
        document.Add(heading);

        var paragraph = new MarkdownParagraph();
        var strong = new MarkdownStrong();
        strong.Inlines.Add(new MarkdownLiteral("by hand"));
        paragraph.Inlines.Add(strong);
        document.Add(paragraph);

        MarkdownText.ToHtml(document).ShouldBe("<h2>Built</h2>\n<p><strong>by hand</strong></p>\n");
        MarkdownText.Write(document).ShouldBe("## Built\n\n**by hand**\n");
    }
}
