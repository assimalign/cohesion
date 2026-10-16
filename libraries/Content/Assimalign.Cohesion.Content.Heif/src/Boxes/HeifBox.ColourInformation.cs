using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The colr leaf with nclx fields or an opaque lazy ICC profile.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class ColourInformationBox : BmffBoxComposite
{
    /// <summary>Creates an empty ColourInformationBox model.</summary>
    public ColourInformationBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x636f6c72;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    // BMFF buffers external leaves. Composite dispatch is the local streaming adapter;
    // these nodes remain semantic leaves and cannot acquire children (see DESIGN.md).
    /// <inheritdoc/>
    public override IList<BmffBox> Children { get; } = Array.AsReadOnly(Array.Empty<BmffBox>());

    /// <summary>Gets or sets the colour representation.</summary>
    public FourCC ColourType { get; set; } = FourCC.FromUInt32(0x6e636c78);

    /// <summary>Gets or sets the colour primaries code.</summary>
    public ushort ColourPrimaries { get; set; }
    /// <summary>Gets or sets the transfer code.</summary>
    public ushort TransferCharacteristics { get; set; }
    /// <summary>Gets or sets the matrix code.</summary>
    public ushort MatrixCoefficients { get; set; }
    /// <summary>Gets or sets the full-range flag.</summary>
    public bool FullRange { get; set; }
    /// <summary>Gets or sets the seven preserved range reserved bits.</summary>
    public byte Reserved { get; set; }
    /// <summary>Gets or sets the lazy ICC bytes for prof/rICC.</summary>
    public BinarySegment Profile { get; set; }
    /// <summary>Gets or sets the lazy suffix for nclx or opaque data for other types.</summary>
    public BinarySegment TrailingData { get; set; }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        ColourType = FourCC.FromUInt32(HeifFields.Read(stream, 4));
        if (ColourType.Value == 0x6e636c78)
        {
            ColourPrimaries = (ushort)HeifFields.Read(stream, 2);
            TransferCharacteristics = (ushort)HeifFields.Read(stream, 2);
            MatrixCoefficients = (ushort)HeifFields.Read(stream, 2);
            var range = HeifFields.Read(stream, 1);
            FullRange = (range & 128) != 0;
            Reserved = (byte)(range & 127);
            TrailingData = HeifFields.Segment(stream);
        }
        else if (ColourType.Value is 0x70726f66 or 0x72494343)
        {
            Profile = HeifFields.Segment(stream);
        }
        else
        {
            TrailingData = HeifFields.Segment(stream);
        }
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        if (Reserved > 127)
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
        }

        HeifFields.Write(stream, ColourType.Value, 4);
        if (ColourType.Value == 0x6e636c78)
        {
            HeifFields.Write(stream, ColourPrimaries, 2);
            HeifFields.Write(stream, TransferCharacteristics, 2);
            HeifFields.Write(stream, MatrixCoefficients, 2);
            HeifFields.Write(stream, (uint)(Reserved | (FullRange ? 128 : 0)), 1);
        }
        if (ColourType.Value is 0x70726f66 or 0x72494343)
        {
            Profile.CopyTo(stream);
        }
        else
        {
            TrailingData.CopyTo(stream);
        }
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() => 4L + (ColourType.Value == 0x6e636c78 ? 7 : 0) + (ColourType.Value is 0x70726f66 or 0x72494343 ? Profile.Length : TrailingData.Length);
}
