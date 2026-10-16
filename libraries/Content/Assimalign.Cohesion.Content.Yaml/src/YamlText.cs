using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Content.Text;
using Assimalign.Cohesion.Content.Yaml.Internal;

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
    /// <exception cref="System.ArgumentNullException">The YAML text is null.</exception>
    public static YamlStream Parse(string text) => Parse(text, null);

    /// <summary>Parses YAML text with resource limits.</summary>
    /// <param name="text">The YAML text.</param>
    /// <param name="options">The limits, or null for defaults.</param>
    /// <returns>The parsed stream.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    /// <exception cref="YamlException">The input is malformed or exceeds a resource limit.</exception>
    public static YamlStream Parse(string text, YamlParserOptions? options)
    {
        ArgumentNullException.ThrowIfNull(text);
        return YamlComposer.Compose(YamlEventParser.Parse(text, options ?? YamlParserOptions.Default), text);
    }

    /// <summary>
    /// Parses YAML from a stream, detecting the Unicode encoding from its leading bytes.
    /// </summary>
    /// <param name="stream">The readable stream carrying the YAML bytes. Read to its end; not disposed.</param>
    /// <returns>The parsed stream of documents.</returns>
    /// <exception cref="YamlException">Thrown when the input is malformed.</exception>
    /// <exception cref="System.NotSupportedException">The source stream does not support reading.</exception>
    /// <exception cref="System.ObjectDisposedException">The source stream has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading the source fails.</exception>
    public static YamlStream Parse(Stream stream) => Parse(stream, null);

    /// <summary>Parses a Unicode byte stream with resource limits.</summary>
    /// <param name="stream">The readable stream. Read to its end; not disposed.</param>
    /// <param name="options">The limits, or null for defaults.</param>
    /// <returns>The parsed stream.</returns>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    /// <exception cref="YamlException">The decoded input is malformed or exceeds a resource limit.</exception>
    /// <exception cref="System.NotSupportedException">The source stream does not support reading.</exception>
    /// <exception cref="System.ObjectDisposedException">The source stream has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Reading the source fails.</exception>
    public static YamlStream Parse(Stream stream, YamlParserOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        (options ?? YamlParserOptions.Default).Validate();

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;

        using var content = StreamContent.Create(buffer, Format, leaveOpen: true);
        using var text = TextContent.Create(content, leaveOpen: true);
        using var reader = text.OpenText();
        return Parse(reader.ReadToEnd(), options);
    }

    /// <summary>
    /// Parses YAML from text content.
    /// </summary>
    /// <param name="content">The text content carrying the YAML.</param>
    /// <returns>The parsed stream of documents.</returns>
    /// <exception cref="YamlException">Thrown when the input is malformed.</exception>
    /// <exception cref="ContentFormatException">The content's strict decoder rejected malformed bytes; the error code is InvalidValue and the position is a byte offset when known.</exception>
    /// <exception cref="ContentException">The content cannot be reopened, or its reader reports a content failure.</exception>
    /// <exception cref="System.ObjectDisposedException">The text content or its source has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">The text content has no readable representation.</exception>
    /// <exception cref="System.IO.IOException">Reading the text content fails.</exception>
    public static YamlStream Parse(ITextContent content) => Parse(content, null);

    /// <summary>Parses text content with resource limits.</summary>
    /// <param name="content">The text content carrying YAML.</param>
    /// <param name="options">The limits, or null for defaults.</param>
    /// <returns>The parsed stream.</returns>
    /// <exception cref="ArgumentNullException">The content is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    /// <exception cref="YamlException">The decoded input is malformed or exceeds a resource limit.</exception>
    /// <exception cref="ContentFormatException">The strict decoder rejected malformed bytes with InvalidValue and a byte position when known.</exception>
    /// <exception cref="ContentException">The content cannot be reopened, or its reader reports a content failure.</exception>
    /// <exception cref="System.ObjectDisposedException">The text content or its source has been disposed.</exception>
    /// <exception cref="System.NotSupportedException">The text content has no readable representation.</exception>
    /// <exception cref="System.IO.IOException">Reading the text content fails.</exception>
    public static YamlStream Parse(ITextContent content, YamlParserOptions? options)
    {
        ArgumentNullException.ThrowIfNull(content);
        (options ?? YamlParserOptions.Default).Validate();
        using var reader = content.OpenText();
        return Parse(reader.ReadToEnd(), options);
    }

    /// <summary>
    /// Parses YAML text that must contain exactly one document.
    /// </summary>
    /// <param name="text">The YAML text.</param>
    /// <returns>The single parsed document.</returns>
    /// <exception cref="YamlException">Thrown when the input is malformed or does not contain exactly one document.</exception>
    /// <exception cref="System.ArgumentNullException">The YAML text is null.</exception>
    public static YamlDocument ParseDocument(string text) => ParseDocument(text, null);

    /// <summary>Parses exactly one YAML document with resource limits.</summary>
    /// <param name="text">The YAML text.</param>
    /// <param name="options">The limits, or null for defaults.</param>
    /// <returns>The single document.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    /// <exception cref="YamlException">The input is malformed, exceeds a limit, or does not contain exactly one document.</exception>
    public static YamlDocument ParseDocument(string text, YamlParserOptions? options)
    {
        var stream = Parse(text, options);
        return stream.Count == 1
            ? stream[0]
            : throw YamlException.At(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DocumentCountInvalid, stream.Count),
                ContentFormatErrorCode.InvalidStructure, 1, 1, text);
    }

    /// <summary>
    /// Parses YAML text into the parse-event pipeline, for streaming consumers that do not need the
    /// document model.
    /// </summary>
    /// <param name="text">The YAML text.</param>
    /// <returns>The events, in document order.</returns>
    /// <exception cref="YamlException">Thrown when the input is malformed.</exception>
    /// <exception cref="System.ArgumentNullException">The YAML text is null.</exception>
    public static IReadOnlyList<YamlEvent> ParseEvents(string text) => ParseEvents(text, null);

    /// <summary>Parses YAML events with the same depth and alias budgets as document parsing.</summary>
    /// <param name="text">The YAML text.</param>
    /// <param name="options">The limits, or null for defaults.</param>
    /// <returns>The events in document order.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    /// <exception cref="YamlException">The input is malformed or exceeds a resource limit.</exception>
    public static IReadOnlyList<YamlEvent> ParseEvents(string text, YamlParserOptions? options)
    {
        ArgumentNullException.ThrowIfNull(text);
        return YamlEventParser.Parse(text, options ?? YamlParserOptions.Default);
    }

    /// <summary>
    /// Writes a stream of documents as YAML text.
    /// </summary>
    /// <param name="stream">The documents to write.</param>
    /// <param name="options">The writer options, or <see langword="null"/> for defaults.</param>
    /// <returns>The YAML text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The indentation size is not positive.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="System.InvalidOperationException">The tree contains a node subtype the emitter does not support.</exception>
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
    /// <exception cref="ArgumentOutOfRangeException">The indentation size is not positive.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="System.InvalidOperationException">The tree contains a node subtype the emitter does not support.</exception>
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
    /// <exception cref="ArgumentOutOfRangeException">The indentation size is not positive.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="output"/> is null.</exception>
    /// <exception cref="System.ArgumentException">The destination does not support writing.</exception>
    /// <exception cref="System.ObjectDisposedException">The destination has been disposed.</exception>
    /// <exception cref="System.IO.IOException">Writing the destination fails.</exception>
    /// <exception cref="System.InvalidOperationException">The tree contains a node subtype the emitter does not support.</exception>
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
    public static IContentReader<YamlStream> CreateReader() => CreateReader(null);

    /// <summary>Creates a content reader with resource limits.</summary>
    /// <param name="options">The limits applied to each read, or null for defaults.</param>
    /// <returns>A reader producing YAML document streams.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its documented range.</exception>
    public static IContentReader<YamlStream> CreateReader(YamlParserOptions? options) => new YamlStreamReader(options ?? YamlParserOptions.Default);

    /// <summary>
    /// Creates a writer implementing the content family's serializer seam.
    /// </summary>
    /// <param name="options">The writer options, or <see langword="null"/> for defaults.</param>
    /// <returns>A writer consuming <see cref="YamlStream"/> documents.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The indentation size is not positive.</exception>
    public static IContentWriter<YamlStream> CreateWriter(YamlWriterOptions? options = null) => new YamlStreamWriter(options ?? YamlWriterOptions.Default);
}
