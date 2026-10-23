using System;
using System.Text;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A byte string retaining its literal or hexadecimal representation (§7.3.4).</summary>
[global::System.Diagnostics.DebuggerDisplay("PdfString")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class PdfString : IPdfObject
{
    private readonly byte[] _bytes;
    private readonly Func<bool>? _isEncrypted;
    private readonly long _position;
    internal PdfString(byte[] bytes, bool isHexadecimal, Func<bool>? isEncrypted = null, long position = 0)
    {
        _bytes = bytes; IsHexadecimal = isHexadecimal; _isEncrypted = isEncrypted; _position = position;
    }
    /// <summary>Creates a string by copying bytes without text conversion.</summary>
    /// <param name="bytes">The decoded bytes.</param>
    /// <param name="isHexadecimal">Whether a writer should use hexadecimal syntax.</param>
    /// <returns>The new string.</returns>
    public static PdfString Create(ReadOnlySpan<byte> bytes, bool isHexadecimal = false) => new(bytes.ToArray(), isHexadecimal);
    /// <summary>Gets the decoded bytes.</summary>
    /// <exception cref="ContentFormatException">Encryption is present (Unsupported).</exception>
    public ReadOnlyMemory<byte> Bytes
    {
        get
        {
            if (_isEncrypted?.Invoke() == true)
            {
                throw new ContentFormatException(ErrorMessages.EncryptionUnsupported, ContentFormatErrorCode.Unsupported, _position);
            }
            return _bytes;
        }
    }
    /// <summary>Gets the original or preferred hexadecimal representation flag.</summary>
    public bool IsHexadecimal { get; }
    /// <summary>Decodes text using an explicit encoding and fallback policy.</summary>
    /// <param name="encoding">The encoding to use.</param>
    /// <returns>The decoded text.</returns>
    /// <exception cref="ArgumentNullException">The encoding is null.</exception>
    /// <exception cref="ContentFormatException">Encryption is present (Unsupported).</exception>
    /// <exception cref="DecoderFallbackException">The encoding rejects the bytes.</exception>
    public string DecodeText(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        return encoding.GetString(Bytes.Span);
    }

    private sealed class DebugView
    {
        private readonly PdfString _target;

        public DebugView(PdfString value)
        {
            _target = value;
        }

        public bool IsHexadecimal => _target.IsHexadecimal;
    }
}
