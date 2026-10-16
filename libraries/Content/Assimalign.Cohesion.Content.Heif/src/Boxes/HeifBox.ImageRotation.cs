using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The irot box in the HEIF item model.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class ImageRotationBox : BmffBox
{
    /// <summary>Creates an empty ImageRotationBox model.</summary>
    public ImageRotationBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x69726f74;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <summary>Gets or sets bytes following the defined fields, preserved verbatim.</summary>
    public byte[] TrailingData { get; set; } = Array.Empty<byte>();
    /// <summary>Gets or sets the counter-clockwise quarter-turn count.</summary>
    public byte Angle { get; set; }
    /// <summary>Gets or sets the preserved high reserved bits.</summary>
    public byte Reserved { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        var value = HeifFields.Read(stream, 1);
        Angle = (byte)(value & 3);
        Reserved = (byte)(value >> 2);
        TrailingData = HeifFields.Bytes(stream, stream.Remaining, Options.MaxBufferedPayloadSize);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        HeifFields.Write(stream, (uint)((Reserved << 2) | Angle), 1);
        stream.Write(TrailingData);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() { Validate(); return 1L + TrailingData.Length; }
    private void Validate()
    {
        if (Angle > 3 || Reserved > 63)
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }
}
