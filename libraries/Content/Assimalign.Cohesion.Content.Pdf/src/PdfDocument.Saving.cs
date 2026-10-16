using System;
using System.IO;

using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf;

public sealed partial class PdfDocument
{
    internal Stream Source => _source;
    internal long SourceLength { get; private set; }
    internal PdfCrossReferenceStyle LastCrossReferenceStyle { get; set; }
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
    }
}
