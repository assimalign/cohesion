using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Ebml;

public sealed partial class EbmlDocument
{
    private static async ValueTask<EbmlDocument> LoadCoreAsync(Stream source, EbmlSchema schema, EbmlReaderOptions options,
        EbmlSchemaRegistry? registry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException(ErrorMessages.SourceSeekable, nameof(source));
        }

        using var reader = EbmlReader.Create(source, schema, options, registry: registry);
        if (!await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new EbmlDataFormatException(ErrorMessages.TruncatedInput, ContentFormatErrorCode.Truncated, reader.Position);
        }

        if (reader.Current.Identifier.EncodedValue != 0x1A45DFA3 || reader.Current.Type != EbmlElementType.MasterElement)
        {
            throw new EbmlDataFormatException(ErrorMessages.HeaderRequired, ContentFormatErrorCode.InvalidStructure, reader.Current.Offset);
        }

        var count = new int[1];
        var document = new EbmlDocument
        {
            Header = new EbmlHeader(await reader.ReadTreeAsync(count, cancellationToken).ConfigureAwait(false)),
            Schema = reader.Schema,
            _maxDepth = options.MaxDepth,
            _lenient = options.Lenient
        };
        while (await reader.ReadNextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.Current.Identifier.EncodedValue == 0x1A45DFA3)
            {
                throw new EbmlDataFormatException(ErrorMessages.HeaderRequired, ContentFormatErrorCode.InvalidStructure, reader.Current.Offset);
            }

            document.Body.Elements.Add(await reader.ReadTreeAsync(count, cancellationToken).ConfigureAwait(false));
        }
        reader.CompleteDocument(document);
        document.Diagnostics = reader.Diagnostics;
        document._source = source;
        return document;
    }

    private async ValueTask SaveCoreAsync(Stream destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(destination, _source))
        {
            throw new ArgumentException(ErrorMessages.InPlaceSave, nameof(destination));
        }

        Schema.Validate(this, _lenient, _maxDepth);
        using var writer = EbmlWriter.Create(destination, Schema, maxDepth: _maxDepth);
        writer.RecalculateCrc = true;
        await writer.WriteElementAsync(Header.Element, cancellationToken).ConfigureAwait(false);
        foreach (var element in Body.Elements)
        {
            await writer.WriteElementAsync(element, cancellationToken).ConfigureAwait(false);
        }
    }
}
