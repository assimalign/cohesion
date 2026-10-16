using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The Copyright box; ISO/IEC 14496-12 8.10.2.</summary>
public sealed class CopyrightBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a CopyrightBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public CopyrightBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.Copyright;

    /// <summary>Gets or sets the ISO-639-2/T language and high padding bit.</summary>
    public ushort PackedLanguage { get; set; } = 0x55c4;
    /// <summary>Gets the decoded three-letter language.</summary>
    public string Language => BmffFields.Language(PackedLanguage);
    private byte[] _notice = Array.Empty<byte>();
    /// <summary>Gets or sets the copyright notice. Original UTF-8 bytes are preserved until assigned.</summary>
    public string Notice { get => Encoding.UTF8.GetString(_notice); set => _notice = Encoding.UTF8.GetBytes(value); }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 0, stream.Offset);
        PackedLanguage = BmffFields.U16(stream);
        _notice = BmffFields.Text(stream);
        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U16(stream, PackedLanguage);
        BmffFields.Text(stream, _notice);
        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 6L + BmffFields.TextSize(_notice, Offset) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 0, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
