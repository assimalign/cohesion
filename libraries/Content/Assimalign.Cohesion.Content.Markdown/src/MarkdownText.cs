using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// Parses, writes, and renders Markdown text: the entry point of the Markdown content format
/// package. The retained syntax is a documented subset of CommonMark 0.31.2 (see the package
/// design documentation); constructs outside the subset degrade predictably to literal text, so
/// parsing never fails for any input.
/// </summary>
public static class MarkdownText
{
    /// <summary>Gets the content format descriptor for Markdown.</summary>
    public static ContentFormat Format { get; } = new()
    {
        Name = "Markdown",
        Kind = ContentKind.Document,
        MediaTypes = ["text/markdown"],
        FileExtensions = [".md", ".markdown"],
        Specification = "https://spec.commonmark.org/0.31.2/"
    };

    /// <summary>
    /// Parses Markdown text into a document.
    /// </summary>
    /// <param name="text">The Markdown text.</param>
    /// <returns>The parsed document. Markdown has no malformed input — every text parses.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static MarkdownDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MarkdownBlockParser.Parse(text);
    }

    /// <summary>
    /// Parses Markdown from a stream, detecting the Unicode encoding from its leading bytes.
    /// </summary>
    /// <param name="stream">The readable stream carrying the Markdown bytes. Read to its end; not disposed.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
    public static MarkdownDocument Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;

        using var content = ContentFactory.FromStream(buffer, Format, leaveOpen: true);
        using var text = TextContentFactory.FromContent(content, leaveOpen: true);
        using var reader = text.OpenText();
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Parses Markdown from text content.
    /// </summary>
    /// <param name="content">The text content carrying the Markdown.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is <see langword="null"/>.</exception>
    public static MarkdownDocument Parse(ITextContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var reader = content.OpenText();
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Renders a document as HTML, in the shapes the CommonMark specification uses.
    /// </summary>
    /// <param name="document">The document to render.</param>
    /// <returns>The HTML text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> is <see langword="null"/>.</exception>
    public static string ToHtml(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return MarkdownHtmlRenderer.Render(document);
    }

    /// <summary>
    /// Parses Markdown text and renders it as HTML in one step.
    /// </summary>
    /// <param name="text">The Markdown text.</param>
    /// <returns>The HTML text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="text"/> is <see langword="null"/>.</exception>
    public static string ToHtml(string text) => ToHtml(Parse(text));

    /// <summary>
    /// Writes a document back to canonical Markdown text. Re-parsing the written text yields an
    /// equivalent document for any parser-produced tree — the package's round-trip guarantee.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <returns>The Markdown text.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="document"/> is <see langword="null"/>.</exception>
    public static string Write(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return MarkdownWriter.Write(document);
    }

    /// <summary>
    /// Writes a document as UTF-8 Markdown to an output stream.
    /// </summary>
    /// <param name="output">The writable output stream. Not disposed.</param>
    /// <param name="document">The document to write.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="output"/> or <paramref name="document"/> is <see langword="null"/>.</exception>
    public static void Write(Stream output, MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(output);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Write(document));
        output.Write(bytes);
    }

    /// <summary>
    /// Creates a reader implementing the content family's parser seam.
    /// </summary>
    /// <returns>A reader producing <see cref="MarkdownDocument"/> values.</returns>
    public static IContentReader<MarkdownDocument> CreateReader() => new MarkdownDocumentReader();

    /// <summary>
    /// Creates a writer implementing the content family's serializer seam.
    /// </summary>
    /// <returns>A writer consuming <see cref="MarkdownDocument"/> values.</returns>
    public static IContentWriter<MarkdownDocument> CreateWriter() => new MarkdownDocumentWriter();
}
