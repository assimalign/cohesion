using System;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A stream dictionary and lazy encoded bytes (§7.3.8); the source must remain open.</summary>
public sealed partial class PdfStream : IPdfObject
{
    private readonly BinarySegment _data;
    private readonly Func<bool>? _isEncrypted;
    internal long Position { get; }
    internal PdfStream(PdfDictionary dictionary, BinarySegment data, Func<bool>? isEncrypted = null, long position = 0)
    {
        Dictionary = dictionary; _data = data; _isEncrypted = isEncrypted; Position = position;
    }
    /// <summary>Creates a stream borrowing encoded bytes.</summary>
    /// <param name="dictionary">The editable stream dictionary.</param>
    /// <param name="data">The already encoded data; filters describe these bytes.</param>
    /// <returns>The stream.</returns>
    /// <exception cref="ArgumentNullException">The dictionary is null.</exception>
    public static PdfStream Create(PdfDictionary dictionary, BinarySegment data)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        return new(dictionary, data);
    }
    /// <summary>Gets the editable dictionary.</summary>
    public PdfDictionary Dictionary { get; }
    /// <summary>Gets lazy encoded bytes without decoding filters.</summary>
    /// <exception cref="ContentFormatException">Encryption is present (Unsupported).</exception>
    public BinarySegment Data
    {
        get
        {
            if (_isEncrypted?.Invoke() == true)
            {
                throw new ContentFormatException(ErrorMessages.EncryptionUnsupported, ContentFormatErrorCode.Unsupported, Position);
            }
            return _data;
        }
    }
}
