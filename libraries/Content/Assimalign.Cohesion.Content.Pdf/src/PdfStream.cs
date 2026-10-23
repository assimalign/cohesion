using System;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A stream dictionary and lazy encoded bytes (§7.3.8); the source must remain open.</summary>
[global::System.Diagnostics.DebuggerDisplay("PdfStream")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed partial class PdfStream : IPdfObject
{
    private BinarySegment _data;
    internal bool IsDataReplaced { get; private set; }
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
    /// <summary>Replaces the payload with borrowed, unencoded bytes for the next save (§7.3.8).</summary>
    /// <param name="data">The replacement bytes; keep their backing memory or source open and unchanged.</param>
    /// <remarks>Removes existing filters and external-file entries. The writer applies Flate by default.
    /// Data and Decode expose the replacement immediately, without encoding it.</remarks>
    /// <exception cref="ContentFormatException">Encryption is present (Unsupported).</exception>
    public void ReplaceData(BinarySegment data)
    {
        _ = Data; // Preserve the security guard even for an empty replacement.
        _data = data; IsDataReplaced = true;
        foreach (var key in new[] { "Filter", "DecodeParms", "F", "FFilter", "FDecodeParms", "DL" })
        {
            Dictionary.Remove(PdfName.Create(System.Text.Encoding.ASCII.GetBytes(key)));
        }
        Dictionary[PdfName.Create("Length"u8)] = PdfInteger.Create(data.Length);
    }
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

    private sealed class DebugView
    {
        private readonly PdfStream _target;

        public DebugView(PdfStream value)
        {
            _target = value;
        }

        public PdfDictionary Dictionary => _target.Dictionary;
    }
}
