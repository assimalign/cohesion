using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Text;

namespace Assimalign.Cohesion.Content.Yaml;

/// <summary>
/// Parses and writes YAML 1.2 text: the entry point of the YAML content format package.
/// </summary>
/// <remarks>
/// Parsing reads the whole input before processing — YAML documents are configuration-sized, and the
/// encoding detection plus multi-document semantics make whole-input processing the predictable
/// choice. Malformed input is reported through <see cref="YamlException"/> with line and column.
/// </remarks>
public static class YamlText
{
    /// <summary>Gets the content format descriptor for YAML.</summary>
    public static ContentFormat Format { get; } = new()
    {
        Name = "YAML",
        Kind = ContentKind.Document,
        MediaTypes = ["application/yaml", "text/yaml"],
        FileExtensions = [".yaml", ".yml"],
        Specification = "https://yaml.org/spec/1.2.2/"
    };

    /// <summary>
    /// Parses YAML text into a stream of documents.
    /// </summary>
    /// <param name="text">The YAML text.</param>
    /// <returns>The parsed stream.</returns>
    /// <exception cref="YamlException">Thrown when the input is malformed.</exception>
    public static YamlStream Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return YamlComposer.Compose(YamlEventParser.Parse(text));
    }

    /// <summary>
    /// Parses YAML from a stream, detecting the Unicode encoding from its leading bytes.
    /// </summary>
    /// <param name="stream">The readable stream carrying the YAML bytes. Read to its end; not disposed.</param>
    /// <returns>The parsed stream of documents.</returns>
    /// <exception cref="YamlException">Thrown when the input is malformed.</exception>
    public static YamlStream Parse(Stream stream)
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
    /// Parses YAML from text content.
    /// </summary>
    /// <param name="content">The text content carrying the YAML.</param>
    /// <returns>The parsed stream of documents.</returns>
    /// <exception cref="YamlException">Thrown when the input is malformed.</exception>
    public static YamlStream Parse(ITextContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var reader = content.OpenText();
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// Parses YAML text that must contain exactly one document.
    /// </summary>
    /// <param name="text">The YAML text.</param>
    /// <returns>The single parsed document.</returns>
    /// <exception cref="YamlException">Thrown when the input is malformed or does not contain exactly one document.</exception>
    public static YamlDocument ParseDocument(string text)
    {
        var stream = Parse(text);
        return stream.Count == 1
            ? stream[0]
            : throw new YamlException($"Expected exactly one document but found {stream.Count}.", 1, 1);
    }

    /// <summary>
    /// Parses YAML text into the parse-event pipeline, for streaming consumers that do not need the
    /// document model.
    /// </summary>
    /// <param name="text">The YAML text.</param>
    /// <returns>The events, in document order.</returns>
    /// <exception cref="YamlException">Thrown when the input is malformed.</exception>
    public static IReadOnlyList<YamlEvent> ParseEvents(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return YamlEventParser.Parse(text);
    }

    /// <summary>
    /// Writes a stream of documents as YAML text.
    /// </summary>
    /// <param name="stream">The documents to write.</param>
    /// <param name="options">The writer options, or <see langword="null"/> for defaults.</param>
    /// <returns>The YAML text.</returns>
    public static string Write(YamlStream stream, YamlWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return YamlNodeEmitter.Emit(stream, options ?? YamlWriterOptions.Default);
    }

    /// <summary>
    /// Writes a single document as YAML text.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="options">The writer options, or <see langword="null"/> for defaults.</param>
    /// <returns>The YAML text.</returns>
    public static string Write(YamlDocument document, YamlWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Write(new YamlStream(document), options);
    }

    /// <summary>
    /// Writes a stream of documents as UTF-8 YAML to an output stream.
    /// </summary>
    /// <param name="output">The writable output stream. Not disposed.</param>
    /// <param name="stream">The documents to write.</param>
    /// <param name="options">The writer options, or <see langword="null"/> for defaults.</param>
    public static void Write(Stream output, YamlStream stream, YamlWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Write(stream, options));
        output.Write(bytes);
    }

    /// <summary>
    /// Creates a reader implementing the content family's parser seam.
    /// </summary>
    /// <returns>A reader producing <see cref="YamlStream"/> documents.</returns>
    public static IContentReader<YamlStream> CreateReader() => new YamlStreamReader();

    /// <summary>
    /// Creates a writer implementing the content family's serializer seam.
    /// </summary>
    /// <param name="options">The writer options, or <see langword="null"/> for defaults.</param>
    /// <returns>A writer consuming <see cref="YamlStream"/> documents.</returns>
    public static IContentWriter<YamlStream> CreateWriter(YamlWriterOptions? options = null) => new YamlStreamWriter(options ?? YamlWriterOptions.Default);
}
