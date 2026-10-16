using System;
using System.IO;

using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf;

public sealed partial class PdfDocument
{
    internal Stream Source => _source;
    internal long SourceLength { get; private set; }
    internal PdfCrossReferenceStyle LastCrossReferenceStyle { get; set; }
    internal PdfString? OriginalFirstIdentifier { get; set; }
    internal Version HeaderVersion { get; private set; } = new(1, 0);
    internal void ValidateSaveDestination(Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Encryption is sticky: removing /Encrypt must not permit ciphertext to be saved as plaintext.
        if (_security.IsEncrypted || IsEncrypted)
        {
            throw new ContentFormatException(ErrorMessages.EncryptionUnsupported, ContentFormatErrorCode.Unsupported, AbsoluteOrigin);
        }
        if (!destination.CanWrite || ReferenceEquals(destination, _source) ||
            destination.CanSeek && destination.Position != destination.Length)
        {
            throw new ArgumentException(ErrorMessages.DestinationInvalid, nameof(destination));
        }
        if (_source.Length != SourceLength)
        {
            throw new ContentFormatException(ErrorMessages.SourceChanged, ContentFormatErrorCode.InvalidStructure, AbsoluteOrigin);
        }
        var ids = Trailer.Get("ID");
        if (ids is not (null or PdfNull) && (ids is not PdfArray array || array.Count != 2 || array[0] is not PdfString || array[1] is not PdfString))
        {
            throw new ContentFormatException(ErrorMessages.IdentifierInvalid, ContentFormatErrorCode.InvalidStructure, AbsoluteOrigin);
        }
        if (OriginalFirstIdentifier is not null && (ids is not PdfArray current ||
            !((PdfString)current[0]).Bytes.Span.SequenceEqual(OriginalFirstIdentifier.Bytes.Span)))
        {
            throw new ContentFormatException(ErrorMessages.IdentifierChanged, ContentFormatErrorCode.InvalidValue, AbsoluteOrigin);
        }
    }
}
