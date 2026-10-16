using System;
using System.Text;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The SchemeType box; ISO/IEC 14496-12 8.12.4.</summary>
public sealed class SchemeTypeBox : BmffFullBox
{
    private byte[] _trailing = Array.Empty<byte>();
    /// <summary>Creates a SchemeTypeBox at its original location.</summary>
    /// <param name="offset">The original absolute header offset.</param>
    /// <param name="limit">The original total size.</param>
    public SchemeTypeBox(long offset, long limit) { SetOriginalLocation(offset, limit);  }

    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.SchemeType;

    /// <summary>Gets or sets the protection scheme FourCC.</summary>
    public FourCC SchemeType { get; set; }
    /// <summary>Gets or sets the scheme version.</summary>
    public uint SchemeVersion { get; set; }
    private byte[] _schemeUri = Array.Empty<byte>();
    /// <summary>Gets or sets the URI when flag 1 is present. Original UTF-8 bytes are preserved until assigned.</summary>
    public string SchemeUri { get => Encoding.UTF8.GetString(_schemeUri); set => _schemeUri = Encoding.UTF8.GetBytes(value); }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        BmffFields.Version(Version, Flags, 0, 1, stream.Offset);
        SchemeType = FourCC.FromUInt32(BmffFields.U32(stream));
        SchemeVersion = BmffFields.U32(stream);
        if ((Flags & 1) != 0)
        {
            _schemeUri = BmffFields.Text(stream);
        }

        _trailing = BmffFields.Bytes(stream, stream.Remaining);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        BmffFields.U32(stream, SchemeType.Value);
        BmffFields.U32(stream, SchemeVersion);
        if ((Flags & 1) != 0)
        {
            BmffFields.Text(stream, _schemeUri);
        }

        stream.Write(_trailing);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        Validate();
        return 12L + ((Flags & 1) != 0 ? BmffFields.TextSize(_schemeUri, Offset) : 0) + _trailing.Length;
    }
    private void Validate()
    {
        BmffFields.Version(Version, Flags, 0, 1, Offset);
    }
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
