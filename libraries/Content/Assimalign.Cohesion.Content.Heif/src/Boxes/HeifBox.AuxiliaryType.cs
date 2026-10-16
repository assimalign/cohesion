using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The auxC box in the HEIF item model.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class AuxiliaryTypeBox : BmffFullBox
{
    /// <summary>Creates an empty AuxiliaryTypeBox model.</summary>
    public AuxiliaryTypeBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x61757843;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <summary>Gets or sets bytes following the defined fields, preserved verbatim.</summary>
    public byte[] TrailingData { get; set; } = Array.Empty<byte>();
    private byte[] _typeBytes = Array.Empty<byte>();
    /// <summary>Gets or sets the auxiliary type URN; original UTF-8 bytes survive until assigned.</summary>
    public string AuxiliaryType { get => System.Text.Encoding.UTF8.GetString(_typeBytes); set => _typeBytes = System.Text.Encoding.UTF8.GetBytes(value); }
    /// <summary>Gets or sets the opaque auxiliary subtype bytes.</summary>
    public byte[] Subtype { get; set; } = Array.Empty<byte>();

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        HeifFields.Version(Version, Flags, 0, 0, stream.Offset);
        var bytes = HeifFields.Bytes(stream, stream.Remaining, Options.MaxBufferedPayloadSize);
        var zero = Array.IndexOf(bytes, (byte)0);
        if (zero < 0)
        {
            throw new ContentFormatException(ErrorMessages.Truncated, ContentFormatErrorCode.Truncated, stream.Offset + stream.Length);
        }

        _typeBytes = bytes.AsSpan(0, zero).ToArray();
        Subtype = bytes.AsSpan(zero + 1).ToArray();
        TrailingData = HeifFields.Bytes(stream, stream.Remaining, Options.MaxBufferedPayloadSize);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        WriteFullBoxHeader(stream);
        stream.Write(_typeBytes);
        stream.WriteByte(0);
        stream.Write(Subtype);
        stream.Write(TrailingData);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() { Validate(); return 5L + _typeBytes.Length + Subtype.Length + TrailingData.Length; }
    private void Validate()
    {
        HeifFields.Version(Version, Flags, 0, 0, Offset);
        if (Array.IndexOf(_typeBytes, (byte)0) >= 0)
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }
}
