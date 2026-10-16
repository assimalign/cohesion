using System;
using System.Text;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>A case-sensitive byte name with equality after #xx unescaping (Â§7.3.5).</summary>
/// <remarks>Names are byte sequences. Empty names are valid; NUL is forbidden.</remarks>
public sealed class PdfName : IPdfObject, IEquatable<PdfName>
{
    private readonly byte[] _bytes;
    private readonly int _hashCode;
    private PdfName(byte[] bytes)
    {
        _bytes = bytes;
        var hash = new HashCode();
        foreach (var value in bytes) { hash.Add(value); }
        _hashCode = hash.ToHashCode();
    }
    /// <summary>Creates a name by copying unescaped bytes.</summary>
    /// <param name="bytes">The bytes without the leading slash.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentException">The name contains NUL.</exception>
    public static PdfName Create(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0)) { throw new ArgumentException(ErrorMessages.NameInvalid, nameof(bytes)); }
        return new PdfName(bytes.ToArray());
    }
    /// <summary>Parses a slash-prefixed name, decoding #xx escapes.</summary>
    /// <param name="token">The complete encoded name.</param>
    /// <returns>The decoded name.</returns>
    /// <exception cref="ContentFormatException">The token is invalid (InvalidValue).</exception>
    public static PdfName Parse(ReadOnlySpan<byte> token)
    {
        if (token.IsEmpty || token[0] != '/')
        {
            throw new ContentFormatException(ErrorMessages.NameInvalid, ContentFormatErrorCode.InvalidValue, 0);
        }
        var bytes = new byte[token.Length - 1];
        var count = 0;
        for (var i = 1; i < token.Length; i++)
        {
            var value = token[i];
            if (value == '#')
            {
                if (i + 2 >= token.Length || Hex(token[i + 1]) < 0 || Hex(token[i + 2]) < 0)
                {
                    throw new ContentFormatException(ErrorMessages.NameInvalid, ContentFormatErrorCode.InvalidValue, i);
                }
                value = (byte)((Hex(token[i + 1]) << 4) | Hex(token[i + 2]));
                i += 2;
            }
            else if (value is 0 or 9 or 10 or 12 or 13 or 32 || value is (byte)'/' or (byte)'%' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']')
            {
                throw new ContentFormatException(ErrorMessages.NameInvalid, ContentFormatErrorCode.InvalidValue, i);
            }
            if (value == 0) { throw new ContentFormatException(ErrorMessages.NameInvalid, ContentFormatErrorCode.InvalidValue, i); }
            bytes[count++] = value;
        }
        return new PdfName(bytes.AsSpan(0, count).ToArray());
    }
    /// <summary>Gets the unescaped bytes.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;
    /// <inheritdoc/>
    public bool Equals(PdfName? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PdfName other && Equals(other);
    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;
    /// <summary>Returns slash-prefixed syntax using uppercase #xx escapes.</summary>
    /// <returns>The encoded name.</returns>
    public override string ToString()
    {
        var result = new StringBuilder("/");
        const string hex = "0123456789ABCDEF";
        foreach (var value in _bytes)
        {
            if (value is >= 33 and <= 126 && value is not ((byte)'#' or (byte)'/' or (byte)'%' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']')) { result.Append((char)value); }
            else { result.Append('#').Append(hex[value >> 4]).Append(hex[value & 15]); }
        }
        return result.ToString();
    }
    internal static int Hex(int value) => value switch
    {
        >= '0' and <= '9' => value - '0', >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10, _ => -1
    };
}
