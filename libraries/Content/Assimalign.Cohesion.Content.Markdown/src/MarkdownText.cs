using System;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Markdown.Internal;
using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// Parses, writes, and renders Markdown text: the entry point of the Markdown content format
/// package. The retained syntax is a documented subset of CommonMark 0.31.2 (see the package
/// design documentation); constructs outside the subset degrade predictably to literal text, so
/// decoded text always parses; strict byte decoding can report a content format failure.
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
    public static MarkdownDocument Parse(string text) => Parse(text, null);

    /// <summary>Parses Markdown with configurable limits that degrade excess syntax to text.</summary>
    /// <param name="text">The Markdown source.</param>
    /// <param name="options">The limits, or null for defaults.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    public static MarkdownDocument Parse(string text, MarkdownParserOptions? options)
    {
        ArgumentNullException.ThrowIfNull(text);
        return MarkdownBlockParser.Parse(text, options ?? MarkdownParserOptions.Default);
    }

    /// <summary>
    /// Parses Markdown from a stream, detecting the Unicode encoding from its leading bytes.
    /// </summary>
    /// <param name="stream">The readable stream carrying the Markdown bytes. Read to its end; not disposed.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.NotSupportedException">The source stream does not support reading.</exception>
    /// <exception cref="System.ObjectDisposedException">The source stream has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading the source fails.</exception>
    public static MarkdownDocument Parse(Stream stream) => Parse(stream, null);

    /// <summary>Parses a Unicode stream with configurable limits that degrade excess syntax to text.</summary>
    /// <param name="stream">The readable stream. Read to its end; not disposed.</param>
    /// <param name="options">The limits, or null for defaults.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    /// <exception cref="System.NotSupportedException">The source stream does not support reading.</exception>
    /// <exception cref="System.ObjectDisposedException">The source stream has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading the source fails.</exception>
    public static MarkdownDocument Parse(Stream stream, MarkdownParserOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        (options ?? MarkdownParserOptions.Default).Validate();

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;

        using var content = StreamContent.Create(buffer, Format, leaveOpen: true);
        using var text = TextContent.Create(content, leaveOpen: true);
        using var reader = text.OpenText();
        return Parse(reader.ReadToEnd(), options);
    }

    /// <summary>
    /// Parses Markdown from text content.
    /// </summary>
    /// <param name="content">The text content carrying the Markdown.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="ContentFormatException">The content's strict decoder rejected malformed bytes; the error code is InvalidValue and the position is a byte offset when known.</exception>
    /// <exception cref="ContentException">The content cannot be reopened, or its reader reports a content failure.</exception>
    /// <exception cref="System.ObjectDisposedException">The text content or its source has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">The text content has no readable representation.</exception>
    /// <exception cref="System.IO.IOException">Reading the text content fails.</exception>
    public static MarkdownDocument Parse(ITextContent content) => Parse(content, null);

    /// <summary>Parses text content with configurable limits that degrade excess syntax to text.</summary>
    /// <param name="content">The text content carrying Markdown.</param>
    /// <param name="options">The limits, or null for defaults.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="ArgumentNullException">The content is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    /// <exception cref="ContentFormatException">A strict decoder rejected malformed bytes with InvalidValue and a byte position when known.</exception>
    /// <exception cref="ContentException">The content cannot be reopened, or its reader reports a content failure.</exception>
    /// <exception cref="System.ObjectDisposedException">The text content or its source has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">The text content has no readable representation.</exception>
    /// <exception cref="System.IO.IOException">Reading the text content fails.</exception>
    public static MarkdownDocument Parse(ITextContent content, MarkdownParserOptions? options)
    {
        ArgumentNullException.ThrowIfNull(content);
        (options ?? MarkdownParserOptions.Default).Validate();
        using var reader = content.OpenText();
        return Parse(reader.ReadToEnd(), options);
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
    /// <exception cref="System.ArgumentException">The destination does not support writing.</exception>
    /// <exception cref="System.ObjectDisposedException">The destination has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Writing the destination fails.</exception>
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
    public static IContentReader<MarkdownDocument> CreateReader() => CreateReader(null);

    /// <summary>Creates a content reader with limits that degrade excess syntax to text.</summary>
    /// <param name="options">The limits for each read, or null for defaults.</param>
    /// <returns>A reader producing Markdown documents.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    public static IContentReader<MarkdownDocument> CreateReader(MarkdownParserOptions? options)
        => new MarkdownDocumentReader(options ?? MarkdownParserOptions.Default);

    /// <summary>
    /// Creates a writer implementing the content family's serializer seam.
    /// </summary>
    /// <returns>A writer consuming <see cref="MarkdownDocument"/> values.</returns>
    public static IContentWriter<MarkdownDocument> CreateWriter() => new MarkdownDocumentWriter();
}
