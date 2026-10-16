using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>An editable EBML header and body retaining lazy binary payloads over its seekable source.</summary>
/// <remarks>The source must remain open and unchanged until the document is disposed. Saving to a separate
/// output copies lazy data through. Concurrent operations over the same source are unsupported.</remarks>
public sealed class EbmlDocument : IDisposable
{
    private Stream? _source;
    private bool _ownsSource;
    private bool _disposed;
    private int _maxDepth = 64;

    /// <summary>Creates an empty document using the RFC header schema.</summary>
    public EbmlDocument() { }

    /// <summary>Gets the editable RFC header.</summary>
    public EbmlHeader Header { get; private set; } = new();
    /// <summary>Gets the ordered editable body.</summary>
    public EbmlBody Body { get; } = new();
    /// <summary>Gets the immutable schema used to resolve and write the document.</summary>
    public EbmlSchema Schema { get; private set; } = EbmlSchema.Header;

    /// <summary>Creates an empty document for a composed schema.</summary>
    /// <param name="schema">The schema, including the RFC header definitions.</param>
    /// <returns>The empty document.</returns>
    /// <exception cref="ArgumentNullException">The schema is null.</exception>
    public static EbmlDocument Create(EbmlSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new EbmlDocument { Schema = schema };
    }

    /// <summary>Loads one document from the current cursor to end of a seekable stream.</summary>
    /// <param name="source">The readable seekable source, borrowed unless leaveOpen is false.</param>
    /// <param name="schema">The composed schema, or the built-in header schema.</param>
    /// <param name="options">Parser and tree limits.</param>
    /// <param name="leaveOpen">Whether disposing the document leaves the source open.</param>
    /// <returns>The editable document.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="ArgumentException">The source is not readable and seekable.</exception>
    /// <exception cref="ContentFormatException">The document is malformed or exceeds a limit.</exception>
    public static EbmlDocument Load(Stream source, EbmlSchema? schema = null, EbmlReaderOptions? options = null, bool leaveOpen = true) =>
        LoadCore(source, schema, options, leaveOpen, CancellationToken.None);

    /// <summary>Saves to a different writable stream, preserving untouched leaf bytes and recomputing master sizes.</summary>
    /// <param name="destination">The writable destination, which remains open.</param>
    /// <exception cref="ArgumentException">The destination is the source or an element cannot be encoded.</exception>
    /// <exception cref="ContentFormatException">A lazy source was truncated.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public void Save(Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(destination, _source))
        {
            throw new ArgumentException(ErrorMessages.InPlaceSave, nameof(destination));
        }

        using var writer = EbmlWriter.Create(destination, Schema, maxDepth: _maxDepth);
        writer.WriteElement(Header.Element);
        foreach (var element in Body.Elements)
        {
            writer.WriteElement(element);
        }
    }

    /// <summary>Creates the format-neutral reader adapter. Its returned documents borrow their source.</summary>
    /// <param name="schema">The composed schema.</param>
    /// <param name="options">Limits copied when the adapter is created.</param>
    /// <returns>The document reader contract.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is invalid.</exception>
    public static IContentReader<EbmlDocument> CreateReader(EbmlSchema? schema = null, EbmlReaderOptions? options = null) =>
        new DocumentReader(schema ?? EbmlSchema.Header, (options ?? new EbmlReaderOptions()).Snapshot());

    /// <summary>Creates the format-neutral writer adapter.</summary>
    /// <returns>The document writer contract.</returns>
    public static IContentWriter<EbmlDocument> CreateWriter() => new DocumentWriter();

    /// <summary>Disposes an explicitly owned source; borrowed sources remain open.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsSource)
        {
            _source?.Dispose();
        }
    }

    private static EbmlDocument LoadCore(Stream source, EbmlSchema? schema, EbmlReaderOptions? options, bool leaveOpen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(ErrorMessages.SourceSeekable, nameof(source));
        }

        var effectiveOptions = (options ?? new EbmlReaderOptions()).Snapshot();
        using var reader = EbmlReader.Create(source, schema, effectiveOptions);
        cancellationToken.ThrowIfCancellationRequested();
        if (!reader.ReadNext())
        {
            throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, reader.Position);
        }

        if (reader.Current.Identifier.EncodedValue != 0x1A45DFA3 || reader.Current.Type != EbmlElementType.MasterElement)
        {
            throw new EbmlDataFormatException(ErrorMessages.HeaderRequired, ContentFormatErrorCode.InvalidStructure, reader.Current.Offset);
        }

        var count = 0;
        var document = new EbmlDocument
        {
            Header = new EbmlHeader(reader.ReadTree(ref count, cancellationToken)),
            Schema = schema ?? EbmlSchema.Header,
            _maxDepth = effectiveOptions.MaxDepth
        };
        while (reader.ReadNext())
        {
            if (reader.Current.Identifier.EncodedValue == 0x1A45DFA3)
            {
                throw new EbmlDataFormatException(ErrorMessages.HeaderRequired, ContentFormatErrorCode.InvalidStructure, reader.Current.Offset);
            }

            document.Body.Elements.Add(reader.ReadTree(ref count, cancellationToken));
        }
        document._source = source;
        document._ownsSource = !leaveOpen; // Ownership transfers only after successful loading.
        return document;
    }

    private sealed class DocumentReader : IContentReader<EbmlDocument>
    {
        private readonly EbmlSchema _schema;
        private readonly EbmlReaderOptions _options;
        internal DocumentReader(EbmlSchema schema, EbmlReaderOptions options) { _schema = schema; _options = options; }
        public EbmlDocument Read(Stream stream) => Load(stream, _schema, _options);
        public ValueTask<EbmlDocument> ReadAsync(Stream stream, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LoadCore(stream, _schema, _options, true, cancellationToken));
    }

    private sealed class DocumentWriter : IContentWriter<EbmlDocument>
    {
        public void Write(Stream stream, EbmlDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            document.Save(stream);
        }
        public ValueTask WriteAsync(Stream stream, EbmlDocument document, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(stream, document);
            return ValueTask.CompletedTask;
        }
    }
}
