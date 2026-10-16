using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif.Internal;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>The clap box in the HEIF item model.</summary>
// Deviates from the repo interface-first rule per the Stage 5 composite-box brief.
public sealed class CleanApertureBox : BmffBox
{
    /// <summary>Creates an empty CleanApertureBox model.</summary>
    public CleanApertureBox() { }
    /// <inheritdoc/>
    public override long Offset => Header.Offset;
    /// <inheritdoc/>
    public override long Limit => Header.TotalSize ?? 0;
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x636c6170;
    internal HeifReaderOptions Options { get; set; } = new();
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <summary>Gets or sets bytes following the defined fields, preserved verbatim.</summary>
    public byte[] TrailingData { get; set; } = Array.Empty<byte>();
    /// <summary>Gets or sets the raw WidthNumerator field.</summary>
    public uint WidthNumerator { get; set; }
    /// <summary>Gets or sets the raw WidthDenominator field.</summary>
    public uint WidthDenominator { get; set; } = 1;
    /// <summary>Gets or sets the raw HeightNumerator field.</summary>
    public uint HeightNumerator { get; set; }
    /// <summary>Gets or sets the raw HeightDenominator field.</summary>
    public uint HeightDenominator { get; set; } = 1;
    /// <summary>Gets or sets the raw HorizontalOffsetNumerator field.</summary>
    public int HorizontalOffsetNumerator { get; set; }
    /// <summary>Gets or sets the raw HorizontalOffsetDenominator field.</summary>
    public uint HorizontalOffsetDenominator { get; set; } = 1;
    /// <summary>Gets or sets the raw VerticalOffsetNumerator field.</summary>
    public int VerticalOffsetNumerator { get; set; }
    /// <summary>Gets or sets the raw VerticalOffsetDenominator field.</summary>
    public uint VerticalOffsetDenominator { get; set; } = 1;

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        WidthNumerator = HeifFields.Read(stream, 4);
        WidthDenominator = HeifFields.Read(stream, 4);
        HeightNumerator = HeifFields.Read(stream, 4);
        HeightDenominator = HeifFields.Read(stream, 4);
        HorizontalOffsetNumerator = unchecked((int)HeifFields.Read(stream, 4));
        HorizontalOffsetDenominator = HeifFields.Read(stream, 4);
        VerticalOffsetNumerator = unchecked((int)HeifFields.Read(stream, 4));
        VerticalOffsetDenominator = HeifFields.Read(stream, 4);
        TrailingData = HeifFields.Bytes(stream, stream.Remaining, Options.MaxBufferedPayloadSize);
        Validate();
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Validate();
        HeifFields.Write(stream, WidthNumerator, 4);
        HeifFields.Write(stream, WidthDenominator, 4);
        HeifFields.Write(stream, HeightNumerator, 4);
        HeifFields.Write(stream, HeightDenominator, 4);
        HeifFields.Write(stream, unchecked((uint)HorizontalOffsetNumerator), 4);
        HeifFields.Write(stream, HorizontalOffsetDenominator, 4);
        HeifFields.Write(stream, unchecked((uint)VerticalOffsetNumerator), 4);
        HeifFields.Write(stream, VerticalOffsetDenominator, 4);
        stream.Write(TrailingData);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() { Validate(); return 32L + TrailingData.Length; }
    private void Validate()
    {
        if (WidthDenominator == 0 || HeightDenominator == 0 || HorizontalOffsetDenominator == 0 || VerticalOffsetDenominator == 0)
        {
            throw new ContentFormatException(ErrorMessages.InvalidValue, ContentFormatErrorCode.InvalidValue, Offset);
        }
    }
}
